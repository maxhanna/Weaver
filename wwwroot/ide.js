'use strict';

angular.module('kanbanApp').factory('IDEMixin', function($http, $timeout, $interval) {
  return {
    init: function(vm, $scope) {
      vm.ide = {
        showSidebar: false,
        openTabs: [],
        currentFile: null,
        currentTab: null,
        dirty: false,
        syncing: false,
        filePickerPath: '',
        filePickerEntries: [],
        filePickerError: '',
        searchFilter: '',
        lastSavedContent: null,
        pendingFileListing: null,
        pendingFileContent: null,
        sharedEditorActive: false,
        sharedFiles: [],
        conflictFiles: {},
        searchQuery: '',
        searchMatches: [],
        searchCurrentIdx: -1,
        searchVisible: false,
        gitDiffVisible: false,
        gitDiffLoading: false,
        gitDiffData: null,
        gitDiffError: '',
        gitDiffView: 'list',
        gitDiffFilePath: '',
        gitDiffRows: [],
        gitCommitMessage: '',
        gitCommitBusy: false,
        gitCommitStatus: '',
        gitCommitResult: '',
        gitCommitError: '',
        gitPrUrl: '',
        left: 60,
        top: 60,
        width: 600,
        height: 400
      };
      var _searchMarks = [];
      var _searchDebounce = null;


      var _contentSyncDebounce = null;

      // ── File-change polling ──────────────────────────────────────────────
      var _pollInterval = null;
      function startFileChangePolling() {
        if (_pollInterval) return;
        _pollInterval = $interval(function () {
          if (!vm.ide.openTabs || vm.ide.openTabs.length === 0) return;
          vm.ide.openTabs.forEach(function (tab) {
            if (!tab.path || !tab.lastModified) return;
            $http.get('/api/editor/check-modified', {
              params: { project: vm.selectedProject || '', path: tab.path, since: tab.lastModified }
            }).then(function (resp) {
              var data = resp.data;
              if (!data || !data.exists) return;
              if (!data.modified) {
                tab.lastModified = data.lastModified;
                return;
              }
              // File was modified externally
              if (tab.dirty) {
                // User has unsaved changes — flag conflict, don't overwrite
                tab.externalModified = true;
                return;
              }
              // No unsaved changes — reload silently
              $http.get('/api/editor/content', {
                params: { project: vm.selectedProject || '', path: tab.path }
              }).then(function (cr) {
                var newContent = cr.data && cr.data.content !== undefined ? cr.data.content : (cr.data || '');
                var wasCurrent = vm.ide.currentFile === tab.path;
                tab.content = newContent;
                tab.savedContent = newContent;
                tab.dirty = false;
                tab.lastModified = cr.data.lastModified || data.lastModified;
                tab.lineCount = (newContent.match(/\n/g) || []).length + 1;
                tab.externalModified = false;
                if (wasCurrent) {
                  vm.ide.dirty = false;
                  if (vm._editor) {
                    vm._editorIgnoreChange = true;
                    var cursor = vm._editor.getCursor();
                    vm._editor.setValue(newContent);
                    vm._editor.setCursor(cursor);
                    vm._editorIgnoreChange = false;
                  }
                }
              });
            });
          });
        }, 3000);
      }
      function stopFileChangePolling() {
        if (_pollInterval) {
          $interval.cancel(_pollInterval);
          _pollInterval = null;
        }
      }

      vm.toggleSidebar = function() {
        vm.ide.showSidebar = !vm.ide.showSidebar;
      };

      vm.openFileBrowser = function() {
        vm.toggleSidebar();
        if (vm.ide.filePickerEntries.length === 0) {
          vm.loadFilePickerEntries();
        }
      };

      vm.loadFilePickerEntries = function() {
        var params = { project: vm.selectedProject || '' };
        if (vm.ide.filePickerPath) {
          params.path = vm.ide.filePickerPath;
        }
        console.log('loadFilePickerEntries', params);
        $http.get('/api/editor/list', { params: params }).then(function(resp) {
          console.log('loadFilePickerEntries response', resp.data);
          vm.ide.filePickerEntries = (resp.data && resp.data.entries) || [];
          vm.ide.filePickerError = '';
        }, function(err) {
          console.log('loadFilePickerEntries error', err);
          vm.ide.filePickerError = (err.data && typeof err.data === 'string' ? err.data : (err.statusText || 'Failed to load files'));
        });
      };

      vm.idePickerEnterDir = function(path) {
        console.log(path);
        vm.ide.filePickerPath = path;
        vm.ide.searchFilter = '';
        vm.loadFilePickerEntries();
      };

      vm.idePickerUpDir = function() {
        if (!vm.ide.filePickerPath) return;
        var segs = vm.ide.filePickerPath.split('/').filter(function(s) { return s && s.length; });
        segs.pop();
        vm.ide.filePickerPath = segs.join('/');
        vm.loadFilePickerEntries();
      };

      vm.openFile = function(path) {
        vm.ide.sharedEditorActive = false;
        var existing = vm.findTab(path);
        if (existing) {
          vm.switchTab(path);
          return;
        }
        var displayName = path.split('/').pop() || path;
        var tab = {
          path: path,
          displayName: displayName,
          content: '',
          savedContent: '',
          dirty: false,
          lineCount: 1,
          remoteEditing: false,
          remoteContent: null,
          fileVersion: 0,
          conflict: false,
          conflictContent: null,
          lastModified: null,
          externalModified: false
        };
        vm.ide.openTabs.push(tab);
        vm.ide.currentFile = path;
        vm.ide.currentTab = tab;
        vm.loadFileContent(path, tab);
      };

      vm.findTab = function(path) {
        for (var i = 0; i < vm.ide.openTabs.length; i++) {
          if (vm.ide.openTabs[i].path === path) return vm.ide.openTabs[i];
        }
        return null;
      };

      vm.switchTab = function(path) {
        var tab = vm.findTab(path);
        if (tab) {
          vm.ide.currentFile = path;
          vm.ide.currentTab = tab;
          vm.ide.dirty = tab.dirty;
          vm.ide.sharedEditorActive = tab.remoteEditing;
          if (vm.bughostedStatus === 'connected') {
            vm.syncEditorState();
          }
        }
      };

      vm.closeTab = function(path, $event) {
        if ($event) $event.stopPropagation();
        var idx = -1;
        for (var i = 0; i < vm.ide.openTabs.length; i++) {
          if (vm.ide.openTabs[i].path === path) { idx = i; break; }
        }
        if (idx === -1) return;
        if (vm.ide.openTabs[idx].dirty) {
          if (!confirm('Unsaved changes to ' + vm.ide.openTabs[idx].displayName + '. Discard?')) return;
        }
        vm.ide.openTabs.splice(idx, 1);
        if (vm.ide.currentFile === path) {
          if (vm.ide.openTabs.length > 0) {
            var newIdx = Math.min(idx, vm.ide.openTabs.length - 1);
            vm.switchTab(vm.ide.openTabs[newIdx].path);
          } else {
            vm.ide.currentFile = null;
            vm.ide.currentTab = null;
            vm.ide.dirty = false;
            vm.ide.sharedEditorActive = false;
            _searchMarks = [];
            vm.ide.searchVisible = false;
            vm.ide.searchQuery = '';
            vm.ide.searchMatches = [];
            vm.ide.searchCurrentIdx = -1;
            // Destroy CodeMirror when last tab closes
            if (vm._editor) {
              var wrapper = vm._editor.getWrapperElement();
              if (wrapper && wrapper.parentNode) wrapper.parentNode.removeChild(wrapper);
              vm._editor = null;
            }
          }
        }
      };

      // ── CodeMirror syntax highlighting ───────────────────────────────
      var MODE_BY_EXT = {
        '.cs': 'text/x-csharp', '.java': 'text/x-java', '.c': 'text/x-csrc',
        '.cpp': 'text/x-c++src', '.h': 'text/x-csrc', '.hpp': 'text/x-c++src',
        '.js': 'text/javascript', '.ts': 'text/typescript', '.jsx': 'text/jsx', '.tsx': 'text/typescript',
        '.html': 'text/html', '.htm': 'text/html', '.xml': 'application/xml', '.svg': 'application/xml',
        '.css': 'text/css', '.scss': 'text/x-scss', '.less': 'text/x-less',
        '.json': 'application/json', '.sql': 'text/x-sql',
        '.py': 'text/x-python', '.rb': 'text/x-ruby', '.php': 'text/x-php',
        '.go': 'text/x-go', '.rs': 'text/x-rust', '.swift': 'text/x-swift',
        '.md': 'text/x-markdown', '.yaml': 'text/x-yaml', '.yml': 'text/x-yaml',
        '.sh': 'text/x-sh', '.bash': 'text/x-sh', '.ps1': 'text/x-sh',
        '.kt': 'text/x-kotlin', '.kts': 'text/x-kotlin'
      };
      function detectMode(path) {
        if (!path) return null;
        var dot = path.lastIndexOf('.');
        if (dot < 0) return null;
        var ext = path.slice(dot).toLowerCase();
        return MODE_BY_EXT[ext] || null;
      }

      vm._editor = null;
      vm._editorIgnoreChange = false;

      function initEditor() {
        var container = document.querySelector('.ide-codemirror-container');
        if (!container) return;
        if (vm._editor) {
          var wrapper = vm._editor.getWrapperElement();
          if (wrapper && wrapper.parentNode) wrapper.parentNode.removeChild(wrapper);
          vm._editor = null;
        }
        vm._editor = CodeMirror(container, {
          value: vm.ide.currentTab ? vm.ide.currentTab.content : '',
          mode: detectMode(vm.ide.currentFile),
          theme: 'weaver-dark',
          lineNumbers: true,
          indentUnit: 2,
          tabSize: 2,
          indentWithTabs: false,
          lineWrapping: false,
          matchBrackets: true,
          autoCloseBrackets: true,
          highlightSelectionMatches: {showToken: false, annotateScrollbar: false},
          extraKeys: {
            'Ctrl-S': function () { vm.saveFile(); },
            'Ctrl-F': function () { if (vm && vm.openSearch) vm.openSearch(); },
            'Cmd-F': function () { if (vm && vm.openSearch) vm.openSearch(); }
          }
        });
        vm._editor.on('change', function () {
          if (vm._editorIgnoreChange) return;
          if (!vm.ide.currentTab) return;
          var val = vm._editor.getValue();
          vm.ide.currentTab.content = val;
          var isDirty = val !== vm.ide.currentTab.savedContent;
          vm.ide.currentTab.dirty = isDirty;
          vm.ide.currentTab.lineCount = (val.match(/\n/g) || []).length + 1;
          vm.ide.dirty = isDirty;
          if (_contentSyncDebounce) { $timeout.cancel(_contentSyncDebounce); }
          _contentSyncDebounce = $timeout(function () {
            if (vm.bughostedStatus === 'connected' && vm.bughostedClientId) {
              vm.syncEditorState();
            }
          }, 500, false);
          if (vm.ide.searchVisible && vm.ide.searchQuery) {
            if (_searchDebounce) $timeout.cancel(_searchDebounce);
            _searchDebounce = $timeout(function () {
              if (vm.ide.searchVisible && vm.ide.searchQuery) {
                vm.doSearch();
              }
            }, 300, false);
          }
        });
        vm._editor.setSize('100%', '100%');
        vm._editor.refresh();
      }

      function setEditorContent(content, path) {
        if (!vm._editor) return;
        _searchMarks.forEach(function (m) { m.clear(); });
        _searchMarks = [];
        vm.ide.searchVisible = false;
        vm.ide.searchQuery = '';
        vm.ide.searchMatches = [];
        vm.ide.searchCurrentIdx = -1;
        vm._editorIgnoreChange = true;
        vm._editor.setValue(content || '');
        var mode = detectMode(path);
        if (mode) vm._editor.setOption('mode', mode);
        vm._editor.clearHistory();
        vm._editorIgnoreChange = false;
      }

      vm.highlightSyntax = function (tab) {
        // CodeMirror handles highlighting natively — this is kept for compatibility
      };

      vm.loadFileContent = function(path, tab) {
        $http.get('/api/editor/content', { params: { project: vm.selectedProject, path: path } }).then(function(resp) {
          var content = resp.data && resp.data.content !== undefined ? resp.data.content : (resp.data || '');
          tab.content = content;
          tab.savedContent = content;
          tab.dirty = false;
          tab.lineCount = (content.match(/\n/g) || []).length + 1;
          tab.fileVersion = 0;
          tab.conflict = false;
          tab.conflictContent = null;
          tab.lastModified = resp.data.lastModified || null;
          tab.externalModified = false;
          vm.ide.dirty = false;
          vm.ide.lastSavedContent = content;
          vm.broadcastFileOpen(path, content);
          // Initialize or update CodeMirror
          $timeout(function () {
            if (!vm._editor) {
              initEditor();
            } else {
              setEditorContent(content, path);
            }
          }, 50);
          if (vm.bughostedStatus === 'connected') {
            $timeout(function() { vm.syncEditorState(); }, 50);
          }
          startFileChangePolling();
        }, function(err) {
          tab.content = '// Error loading file: ' + (err.statusText || 'Unknown error');
          tab.savedContent = '';
          tab.dirty = false;
          tab.lineCount = 1;
          vm.ide.dirty = false;
          $timeout(function () {
            if (!vm._editor) {
              initEditor();
            } else {
              setEditorContent(tab.content, path);
            }
          }, 50);
        });
      };

      vm.onContentChange = function() {
        // Content changes are now handled by CodeMirror's change event
      };

      // Re-init editor when tab switches (ng-if may recreate DOM)
      var _origSwitchTab = vm.switchTab;
      vm.switchTab = function (path) {
        _origSwitchTab(path);
        var tab = vm.ide.currentTab;
        if (tab && (tab.type === 'file' || !tab.type)) {
          $timeout(function () {
            if (vm.ide.currentTab) {
              if (!vm._editor) {
                initEditor();
              } else {
                setEditorContent(vm.ide.currentTab.content, path);
              }
            }
          }, 50);
        } else if (tab) {
          // Non-file tab — destroy CodeMirror if it exists to free resources
          if (vm._editor) {
            var wrapper = vm._editor.getWrapperElement();
            if (wrapper && wrapper.parentNode) wrapper.parentNode.removeChild(wrapper);
            vm._editor = null;
          }
        }
      };

      vm.saveFile = function() {
        if (!vm.ide.currentFile || !vm.ide.currentTab) return;
        var tab = vm.ide.currentTab;
        var content = tab.content;

        if (tab.conflict) {
          if (!confirm('This file has been edited remotely while you had unsaved changes. Saving will overwrite the remote version. Continue?')) return;
        }

        var payload = {
          project: vm.selectedProject,
          path: vm.ide.currentFile,
          content: content
        };
        $http.post('/api/editor/save', payload).then(function() {
          tab.fileVersion = (tab.fileVersion || 0) + 1;
          tab.savedContent = content;
          tab.dirty = false;
          tab.conflict = false;
          tab.conflictContent = null;
          vm.ide.dirty = false;
          vm.ide.lastSavedContent = content;
          vm.broadcastFileSave(vm.ide.currentFile, content);
        }, function(err) {
          console.error('Failed to save file:', err);
        });
      };

      vm.newFile = function() {
        var fileName = prompt('Enter new file name (relative to project root):');
        if (!fileName) return;
        var fullPath = vm.ide.filePickerPath ? vm.ide.filePickerPath + '/' + fileName : fileName;
        var existing = vm.findTab(fullPath);
        if (existing) {
          vm.switchTab(fullPath);
          return;
        }
        var displayName = fileName.split('/').pop() || fileName;
        var tab = {
          path: fullPath,
          displayName: displayName,
          content: '',
          savedContent: '',
          dirty: true,
          lineCount: 1,
          remoteEditing: false,
          remoteContent: null,
          fileVersion: 0,
          conflict: false,
          conflictContent: null,
          lastModified: null,
          externalModified: false
        };
        vm.ide.openTabs.push(tab);
        vm.ide.currentFile = fullPath;
        vm.ide.currentTab = tab;
        vm.ide.dirty = true;
        $timeout(function () {
          if (!vm._editor) {
            initEditor();
          } else {
            setEditorContent('', fullPath);
          }
        }, 50);
      };

      vm.closeFile = function() {
        if (vm.ide.currentTab && vm.ide.currentTab.dirty) {
          if (!confirm('Unsaved changes to ' + vm.ide.currentTab.displayName + '. Discard?')) return;
        }
        vm.closeTab(vm.ide.currentFile);
      };

      vm.clearSearch = function() {
        vm.ide.searchFilter = '';
        vm.loadFilePickerEntries();
      };

      vm.onSearchChange = function() {
        if (vm.ide.searchFilter && vm.ide.searchFilter.trim()) {
          vm.loadFilePickerEntries();
        } else {
          vm.loadFilePickerEntries();
        }
      };

      vm.closeIDE = function() {
        var hasDirty = false;
        for (var i = 0; i < vm.ide.openTabs.length; i++) {
          if (vm.ide.openTabs[i].dirty) { hasDirty = true; break; }
        }
        if (hasDirty && !confirm('You have unsaved changes. Close anyway?')) return;
        if (vm._editor) {
          var wrapper = vm._editor.getWrapperElement();
          if (wrapper && wrapper.parentNode) wrapper.parentNode.removeChild(wrapper);
          vm._editor = null;
        }
        vm.ide.openTabs = [];
        vm.ide.currentFile = null;
        vm.ide.currentTab = null;
        vm.ide.dirty = false;
        vm.ide.filePickerPath = '';
        vm.ide.filePickerEntries = [];
        vm.ide.searchFilter = '';
        vm.ide.sharedEditorActive = false;
        vm.ide.sharedFiles = [];
        _searchMarks = [];
        vm.ide.searchVisible = false;
        vm.ide.searchQuery = '';
        vm.ide.searchMatches = [];
        vm.ide.searchCurrentIdx = -1;
        stopFileChangePolling();
        vm.showIDE = false;
      };

      // ── Search ─────────────────────────────────────────────────────────
      vm.openSearch = function () {
        vm.ide.searchVisible = true;
        vm.ide.searchQuery = '';
        vm.ide.searchMatches = [];
        vm.ide.searchCurrentIdx = -1;
        $timeout(function () {
          var input = document.querySelector('.ide-search-input');
          if (input) { input.focus(); input.select(); }
          if (vm._editor) {
            var sel = vm._editor.getSelection();
            if (sel) {
              vm.ide.searchQuery = sel;
              vm.doSearch();
            }
          }
        });
        // Force a second focus attempt after ng-if renders the DOM
        $timeout(function () {
          var input = document.querySelector('.ide-search-input');
          if (input) input.focus();
        }, 100);
      };

      vm.closeSearch = function () {
        vm.ide.searchVisible = false;
        _searchMarks.forEach(function (m) { m.clear(); });
        _searchMarks = [];
        vm.ide.searchMatches = [];
        vm.ide.searchCurrentIdx = -1;
        vm.ide.searchQuery = '';
        if (vm._editor) vm._editor.focus();
      };

      vm.doSearch = function () {
        _searchMarks.forEach(function (m) { m.clear(); });
        _searchMarks = [];
        vm.ide.searchMatches = [];
        vm.ide.searchCurrentIdx = -1;
        var query = vm.ide.searchQuery;
        if (!query || !vm._editor) return;
        try {
          var cur = vm._editor.getSearchCursor(query, { line: 0, ch: 0 });
          while (cur.findNext()) {
            vm.ide.searchMatches.push({ from: cur.from(), to: cur.to() });
            var mark = vm._editor.markText(cur.from(), cur.to(), { className: 'cm-search-match' });
            _searchMarks.push(mark);
          }
        } catch (e) {
          return;
        }
        if (vm.ide.searchMatches.length > 0) {
          vm.ide.searchCurrentIdx = 0;
          vm._editor.setSelection(vm.ide.searchMatches[0].from, vm.ide.searchMatches[0].to);
          vm._editor.scrollIntoView({ from: vm.ide.searchMatches[0].from, to: vm.ide.searchMatches[0].to });
        }
      };

      vm.searchNext = function () {
        if (vm.ide.searchMatches.length === 0) return;
        var idx = vm.ide.searchCurrentIdx + 1;
        if (idx >= vm.ide.searchMatches.length) idx = 0;
        vm.ide.searchCurrentIdx = idx;
        var match = vm.ide.searchMatches[idx];
        vm._editor.setSelection(match.from, match.to);
        vm._editor.scrollIntoView({ from: match.from, to: match.to });
      };

      vm.searchPrev = function () {
        if (vm.ide.searchMatches.length === 0) return;
        var idx = vm.ide.searchCurrentIdx - 1;
        if (idx < 0) idx = vm.ide.searchMatches.length - 1;
        vm.ide.searchCurrentIdx = idx;
        var match = vm.ide.searchMatches[idx];
        vm._editor.setSelection(match.from, match.to);
        vm._editor.scrollIntoView({ from: match.from, to: match.to });
      };

      vm.onSearchKeydown = function ($event) {
        if ($event.key === 'Enter') {
          if ($event.shiftKey) {
            vm.searchPrev();
          } else {
            vm.searchNext();
          }
          $event.preventDefault();
        } else if ($event.key === 'Escape') {
          vm.closeSearch();
          $event.preventDefault();
        }
      };

      // ── Git Diff as IDE tabs ──────────────────────────────────────────
      function _openGitTab(type, displayName, pathKey) {
        // Reuse existing git tab with same pathKey, or create one
        var existing = null;
        for (var i = 0; i < vm.ide.openTabs.length; i++) {
          if (vm.ide.openTabs[i].gitTabKey === pathKey) { existing = vm.ide.openTabs[i]; break; }
        }
        if (existing) {
          vm.switchTab(existing.path);
          return existing;
        }
        var tab = {
          type: type,
          path: pathKey || '_git:' + type,
          gitTabKey: pathKey || '_git:' + type,
          displayName: displayName,
          content: '',
          savedContent: '',
          dirty: false,
          lineCount: 1,
          remoteEditing: false,
          gitData: null,
          gitLoading: false,
          gitError: '',
          gitFilePath: '',
          gitRows: [],
          gitCommitMessage: '',
          gitCommitResult: '',
          gitCommitError: '',
          gitCommitBusy: false,
          gitCommitStatus: '',
          gitPrUrl: '',
          lastModified: null,
          externalModified: false
        };
        vm.ide.openTabs.push(tab);
        vm.ide.currentFile = tab.path;
        vm.ide.currentTab = tab;
        vm.ide.dirty = false;
        return tab;
      }

      vm.showGitDiff = function () {
        var tab = _openGitTab('git-list', 'Source Control', '_git:list');
        tab.gitLoading = true;
        tab.gitData = null;
        tab.gitError = '';
        tab.gitCommitMessage = '';
        tab.gitCommitResult = '';
        tab.gitCommitError = '';
        tab.gitPrUrl = '';
        $http.get('/api/editor/git-diff', { params: { project: vm.selectedProject } }).then(function (resp) {
          tab.gitLoading = false;
          tab.gitData = resp.data;
        }, function (err) {
          tab.gitLoading = false;
          tab.gitError = (err.data && err.data.error) || err.statusText || 'Failed to load git diff';
        });
      };

      vm.showFileDiff = function (path) {
        var displayName = 'Diff: ' + (path.split('/').pop() || path);
        var tab = _openGitTab('git-diff', displayName, '_git:diff:' + path);
        tab.gitFilePath = path;
        tab.gitLoading = true;
        tab.gitRows = [];
        tab.gitError = '';
        $http.get('/api/editor/git-diff-file', { params: { project: vm.selectedProject, path: path } }).then(function (resp) {
          tab.gitLoading = false;
          var data = resp.data;
          tab.gitRows = vm.computeLineDiff(data.oldContent || '', data.newContent || '');
        }, function (err) {
          tab.gitLoading = false;
          tab.gitError = (err.data && err.data.error) || err.statusText || 'Failed to load file diff';
        });
      };

      vm.backToSourceControl = function () {
        // Close current diff-content tab if present, then open/reuse git-list tab
        var curTab = vm.ide.currentTab;
        if (curTab && curTab.type === 'git-diff') {
          vm.closeTab(curTab.path);
        }
        vm.showGitDiff();
      };

      // ── Line diff algorithm (LCS-based) ───────────────────────────────
      vm.computeLineDiff = function (oldText, newText) {
        var oldLines = oldText.replace(/\r\n/g, '\n').split('\n');
        var newLines = newText.replace(/\r\n/g, '\n').split('\n');

        // Build LCS table
        var m = oldLines.length, n = newLines.length;
        var dp = [];
        for (var i = 0; i <= m; i++) {
          dp[i] = new Array(n + 1).fill(0);
        }
        for (var i = 1; i <= m; i++) {
          for (var j = 1; j <= n; j++) {
            if (oldLines[i - 1] === newLines[j - 1]) {
              dp[i][j] = dp[i - 1][j - 1] + 1;
            } else {
              dp[i][j] = Math.max(dp[i - 1][j], dp[i][j - 1]);
            }
          }
        }

        // Backtrack to build diff rows
        var rows = [];
        var i = m, j = n;
        var tempRows = [];
        while (i > 0 || j > 0) {
          if (i > 0 && j > 0 && oldLines[i - 1] === newLines[j - 1]) {
            tempRows.push({ type: 'equal', oldNum: i, oldContent: oldLines[i - 1], newNum: j, newContent: newLines[j - 1] });
            i--; j--;
          } else if (j > 0 && (i === 0 || dp[i][j - 1] >= dp[i - 1][j])) {
            tempRows.push({ type: 'add', oldNum: null, oldContent: '', newNum: j, newContent: newLines[j - 1] });
            j--;
          } else if (i > 0) {
            tempRows.push({ type: 'remove', oldNum: i, oldContent: oldLines[i - 1], newNum: null, newContent: '' });
            i--;
          }
        }
        // Reverse to get chronological order
        for (var k = tempRows.length - 1; k >= 0; k--) {
          rows.push(tempRows[k]);
        }
        return rows;
      };

      vm.backToSourceControl = function () {
        vm.ide.gitDiffView = 'list';
        vm.ide.gitDiffFilePath = '';
        vm.ide.gitDiffRows = [];
        vm.showGitDiff();
      };

      function _findGitListTab() {
        for (var i = 0; i < vm.ide.openTabs.length; i++) {
          if (vm.ide.openTabs[i].type === 'git-list') return vm.ide.openTabs[i];
        }
        return null;
      }

      function _doGitCommit(pushAfter, createPr) {
        var tab = _findGitListTab() || vm.ide.currentTab;
        tab.gitCommitResult = '';
        tab.gitCommitError = '';
        tab.gitPrUrl = '';
        tab.gitCommitBusy = true;
        tab.gitCommitStatus = 'Committing';
        var payload = { project: vm.selectedProject, message: tab.gitCommitMessage };
        $http.post('/api/editor/git-commit', payload).then(function (resp) {
          if (resp.data.nothingToCommit) {
            tab.gitCommitBusy = false;
            tab.gitCommitError = 'Nothing to commit — no changes staged or unstaged';
            return;
          }
          if (!resp.data.success) {
            tab.gitCommitBusy = false;
            tab.gitCommitError = resp.data.commitOutput || resp.data.error || 'Commit failed';
            return;
          }
          if (pushAfter || createPr) {
            tab.gitCommitStatus = 'Pushing';
            $http.post('/api/editor/git-push', payload).then(function (pushResp) {
              if (createPr) {
                tab.gitCommitStatus = 'Creating PR';
                $http.post('/api/editor/git-pr', payload).then(function (prResp) {
                  tab.gitCommitBusy = false;
                  if (prResp.data.success) {
                    tab.gitCommitResult = 'PR created successfully';
                    tab.gitPrUrl = prResp.data.prUrl || '';
                    tab.gitCommitMessage = '';
                    vm.showGitDiff();
                  } else {
                    tab.gitCommitError = prResp.data.prUrl || prResp.data.error || 'PR creation failed';
                  }
                }, function (err) {
                  tab.gitCommitBusy = false;
                  tab.gitCommitError = 'PR creation failed: ' + (err.statusText || '');
                });
              } else {
                tab.gitCommitBusy = false;
                tab.gitCommitResult = 'Committed and pushed successfully';
                tab.gitCommitMessage = '';
                vm.showGitDiff();
              }
            }, function (err) {
              tab.gitCommitBusy = false;
              tab.gitCommitError = 'Push failed: ' + (err.statusText || '');
            });
          } else {
            tab.gitCommitBusy = false;
            tab.gitCommitResult = 'Committed successfully';
            tab.gitCommitMessage = '';
            vm.showGitDiff();
          }
        }, function (err) {
          tab.gitCommitBusy = false;
          tab.gitCommitError = (err.data && err.data.error) || err.statusText || 'Commit failed';
        });
      }

      vm.gitCommit = function () { _doGitCommit(false, false); };
      vm.gitCommitAndPush = function () { _doGitCommit(true, false); };
      vm.gitCreatePr = function () { _doGitCommit(true, true); };

      // ===== Shared editing via BugHosted =====
      vm.broadcastFileOpen = function(path, content) {
        if (vm.bughostedStatus !== 'connected' || !vm.bughostedClientId) return;
        vm.ide.lastSharedFile = path;
        vm.ide.syncing = true;
      };

      vm.broadcastFileSave = function(path, content) {
        if (vm.bughostedStatus !== 'connected' || !vm.bughostedClientId) return;
        vm.ide.syncing = true;
        $http.post('/api/bughosted/fileEdit', {
          clientId: vm.bughostedClientId,
          path: path,
          content: content
        }).then(function() {
          vm.ide.syncing = false;
        }, function() {
          vm.ide.syncing = false;
        });
      };

      vm.handleRemoteFileEdit = function(params) {
        if (!params || !params.path || params.content === undefined) return;
        var tab = vm.findTab(params.path);
        if (!tab) return;
        if (tab.dirty && tab.content !== params.content) {
          tab.conflict = true;
          tab.conflictContent = params.content;
          vm.ide.dirty = true;
          if (vm.ide.currentFile === tab.path) {
            vm.ide.sharedEditorActive = true;
          }
          return;
        }
        tab.content = params.content;
        tab.savedContent = params.content;
        tab.dirty = false;
        tab.fileVersion = (tab.fileVersion || 0) + 1;
        tab.conflict = false;
        tab.conflictContent = null;
        tab.remoteEditing = true;
        tab.lineCount = (params.content.match(/\n/g) || []).length + 1;
        if (vm.ide.currentFile === params.path) {
          vm.ide.dirty = false;
          vm.ide.sharedEditorActive = true;
        }
        vm.ide.syncing = false;
      };

      vm.applyRemoteContent = function(path, content) {
        var tab = vm.findTab(path);
        if (!tab) return;
        if (tab.dirty) {
          tab.conflict = true;
          tab.conflictContent = content;
          return;
        }
        tab.content = content;
        tab.savedContent = content;
        tab.dirty = false;
        tab.fileVersion = (tab.fileVersion || 0) + 1;
        tab.lineCount = (content.match(/\n/g) || []).length + 1;
        if (vm.ide.currentFile === path) {
          vm.ide.dirty = false;
        }
      };

      vm.reloadExternalFile = function(path) {
        var tab = vm.findTab(path);
        if (!tab) return;
        $http.get('/api/editor/content', { params: { project: vm.selectedProject || '', path: path } }).then(function(resp) {
          var content = resp.data && resp.data.content !== undefined ? resp.data.content : (resp.data || '');
          var wasCurrent = vm.ide.currentFile === path;
          tab.content = content;
          tab.savedContent = content;
          tab.dirty = false;
          tab.lastModified = resp.data.lastModified || null;
          tab.externalModified = false;
          tab.lineCount = (content.match(/\n/g) || []).length + 1;
          if (wasCurrent) {
            vm.ide.dirty = false;
            if (vm._editor) {
              vm._editorIgnoreChange = true;
              var cursor = vm._editor.getCursor();
              vm._editor.setValue(content);
              vm._editor.setCursor(cursor);
              vm._editorIgnoreChange = false;
            }
          }
        });
      };

      vm.resolveConflict = function(path) {
        var tab = vm.findTab(path);
        if (!tab || !tab.conflict) return;
        var choice = confirm('Use local version? Click Cancel to use remote version.');
        if (choice) {
          tab.conflict = false;
          tab.conflictContent = null;
        } else {
          tab.content = tab.conflictContent;
          tab.savedContent = tab.conflictContent;
          tab.dirty = false;
          tab.conflict = false;
          tab.conflictContent = null;
          tab.fileVersion = (tab.fileVersion || 0) + 1;
          if (vm.ide.currentFile === path) {
            vm.ide.dirty = false;
          }
        }
      };

      vm.resolveAllConflicts = function() {
        for (var i = 0; i < vm.ide.openTabs.length; i++) {
          if (vm.ide.openTabs[i].conflict) {
            vm.resolveConflict(vm.ide.openTabs[i].path);
          }
        }
      };

      vm.startDrag = function($event) {
        // Only drag on the header itself, not buttons/inputs inside it
        if ($event.target.tagName === 'BUTTON' || $event.target.tagName === 'INPUT' ||
            $event.target.tagName === 'TEXTAREA' || $event.target.closest('button')) return;
        $event.preventDefault();
        var startX = $event.clientX;
        var startY = $event.clientY;
        var startLeft = vm.ide.left;
        var startTop = vm.ide.top;
        var viewW = window.innerWidth;
        var viewH = window.innerHeight;
        function onMove(e) {
          vm.ide.left = Math.max(0, Math.min(viewW - 100, startLeft + (e.clientX - startX)));
          vm.ide.top = Math.max(0, Math.min(viewH - 60, startTop + (e.clientY - startY)));
          $scope.$digest();
        }
        function onUp() {
          document.removeEventListener('mousemove', onMove);
          document.removeEventListener('mouseup', onUp);
        }
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
      };

      vm.startResize = function(dir, $event) {
        $event.preventDefault();
        var startX = $event.clientX;
        var startY = $event.clientY;
        var startW = vm.ide.width;
        var startH = vm.ide.height;
        var startLeft = vm.ide.left;
        var startTop = vm.ide.top;
        var minW = 300, minH = 200;
        var viewW = window.innerWidth;
        var viewH = window.innerHeight;
        function onMove(e) {
          var dx = e.clientX - startX;
          var dy = e.clientY - startY;
          if (dir === 'e' || dir === 'se') {
            vm.ide.width = Math.max(minW, Math.min(viewW - vm.ide.left, startW + dx));
          }
          if (dir === 's' || dir === 'se') {
            vm.ide.height = Math.max(minH, Math.min(viewH - vm.ide.top, startH + dy));
          }
          $scope.$digest();
        }
        function onUp() {
          document.removeEventListener('mousemove', onMove);
          document.removeEventListener('mouseup', onUp);
        }
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
      };
    }
  };
});
