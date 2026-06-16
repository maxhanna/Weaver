using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace Weaver.Hubs;

/// <summary>
    /// SignalR hub for real-time co-editing of files.
    ///
    /// Flow:
    ///   1. A client (local Weaver UI or remote BugHosted IDE) calls JoinFile(path).
    ///   2. The hub places that connection in a group keyed by the canonical file path.
    ///   3. When any participant calls PushContent(path, content, version), all others in
    ///      the same group receive OnContentChanged.
    ///   4. Cursor/selection positions are broadcast via PushCursor and forwarded as
    ///      OnCursorChanged to all OTHER participants (not the sender).
    ///   5. On disconnect, the participant is removed from all groups and others are notified.
    /// </summary>
    public class CoEditHub : Hub
    {
        // Maps connectionId → set of file paths that connection is editing
        private static readonly ConcurrentDictionary<string, HashSet<string>> _connectionFiles = new();

        // Maps filePath → dict of connectionId → CursorInfo (for presence)
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CursorInfo>> _fileCursors = new();

        // Maps connectionId → display name
        private static readonly ConcurrentDictionary<string, string> _connectionNames = new();

        /// <summary>
        /// Join the co-edit session for a specific file.
        /// Returns the current content (if another participant has pushed it) or null.
        /// </summary>
        public async Task JoinFile(string filePath, string displayName)
        {
            var groupKey = NormalizeGroupKey(filePath);
            await Groups.AddToGroupAsync(Context.ConnectionId, groupKey);

            // Track which files this connection has open
            _connectionFiles.AddOrUpdate(
                Context.ConnectionId,
                _ => new HashSet<string> { groupKey },
                (_, set) => { lock (set) { set.Add(groupKey); } return set; }
            );

            _connectionNames[Context.ConnectionId] = displayName;

            // Register cursor slot
            var cursors = _fileCursors.GetOrAdd(groupKey, _ => new ConcurrentDictionary<string, CursorInfo>());
            cursors[Context.ConnectionId] = new CursorInfo { DisplayName = displayName };

            // Notify others that this user joined
            await Clients.OthersInGroup(groupKey).SendAsync("OnParticipantJoined", new
            {
                connectionId = Context.ConnectionId,
                displayName,
                filePath = groupKey
            });

            // Send the joiner the list of current participants in this file
            var participants = cursors
                .Where(kvp => kvp.Key != Context.ConnectionId)
                .Select(kvp => new { connectionId = kvp.Key, displayName = kvp.Value.DisplayName, cursor = kvp.Value })
                .ToList();

            await Clients.Caller.SendAsync("OnCurrentParticipants", new { filePath = groupKey, participants });
        }

        /// <summary>
        /// Leave the co-edit session for a specific file.
        /// </summary>
        public async Task LeaveFile(string filePath)
        {
            var groupKey = NormalizeGroupKey(filePath);
            await LeaveFileInternal(Context.ConnectionId, groupKey);
        }

        /// <summary>
        /// Push the full file content to all co-editors.
        /// version: monotonic integer — clients should discard updates with lower versions.
        /// </summary>
        public async Task PushContent(string filePath, string content, long version)
        {
            var groupKey = NormalizeGroupKey(filePath);
            await Clients.OthersInGroup(groupKey).SendAsync("OnContentChanged", new
            {
                filePath = groupKey,
                content,
                version,
                from = Context.ConnectionId,
                fromName = _connectionNames.GetValueOrDefault(Context.ConnectionId, "unknown")
            });
        }

        /// <summary>
        /// Push cursor / selection position to all co-editors in the same file.
        /// line/column are 0-based. selectionEnd is null for a collapsed cursor.
        /// </summary>
        public async Task PushCursor(string filePath, int line, int column, int? selectionEndLine, int? selectionEndColumn)
        {
            var groupKey = NormalizeGroupKey(filePath);

            // Update stored cursor
            if (_fileCursors.TryGetValue(groupKey, out var cursors))
            {
                cursors.AddOrUpdate(
                    Context.ConnectionId,
                    _ => new CursorInfo
                    {
                        DisplayName = _connectionNames.GetValueOrDefault(Context.ConnectionId, "unknown"),
                        Line = line, Column = column,
                        SelectionEndLine = selectionEndLine, SelectionEndColumn = selectionEndColumn
                    },
                    (_, existing) =>
                    {
                        existing.Line = line; existing.Column = column;
                        existing.SelectionEndLine = selectionEndLine; existing.SelectionEndColumn = selectionEndColumn;
                        return existing;
                    }
                );
            }

            await Clients.OthersInGroup(groupKey).SendAsync("OnCursorChanged", new
            {
                filePath = groupKey,
                connectionId = Context.ConnectionId,
                displayName = _connectionNames.GetValueOrDefault(Context.ConnectionId, "unknown"),
                line, column, selectionEndLine, selectionEndColumn
            });
        }

        // ─── Disconnect cleanup ────────────────────────────────────────────────

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (_connectionFiles.TryRemove(Context.ConnectionId, out var files))
            {
                foreach (var groupKey in files)
                    await LeaveFileInternal(Context.ConnectionId, groupKey);
            }

            _connectionNames.TryRemove(Context.ConnectionId, out _);
            await base.OnDisconnectedAsync(exception);
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private async Task LeaveFileInternal(string connectionId, string groupKey)
        {
            await Groups.RemoveFromGroupAsync(connectionId, groupKey);

            if (_fileCursors.TryGetValue(groupKey, out var cursors))
            {
                cursors.TryRemove(connectionId, out _);
                if (cursors.IsEmpty)
                    _fileCursors.TryRemove(groupKey, out _);
            }

            if (_connectionFiles.TryGetValue(connectionId, out var files))
                lock (files) { files.Remove(groupKey); }

            await Clients.Group(groupKey).SendAsync("OnParticipantLeft", new
            {
                connectionId,
                filePath = groupKey
            });
        }

        /// <summary>
        /// Normalize a file path to a stable group key (lowercase, forward slashes).
        /// </summary>
        private static string NormalizeGroupKey(string filePath)
            => filePath.Replace('\\', '/').ToLowerInvariant().Trim('/');
    }

    public class CursorInfo
    {
        public string DisplayName { get; set; } = "";
        public int Line { get; set; }
        public int Column { get; set; }
        public int? SelectionEndLine { get; set; }
        public int? SelectionEndColumn { get; set; }
    }
