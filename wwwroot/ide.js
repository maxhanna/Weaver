'use strict';

angular.module('kanbanApp').factory('IDEMixin', function($http, $timeout) {
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
        left: 60,
        top: 60,
        width: 600,
        height: 400
      };


      var _contentSyncDebounce = null;

      vm.toggleSidebar = function() {
        vm.ide.showSidebar = !vm.ide.showSidebar;
      };

      vm.openFileBrowser = function() {
        vm.ide.showSidebar = true;
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
          conflictContent: null
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
          }
        }
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
          vm.ide.dirty = false;
          vm.ide.lastSavedContent = content;
          vm.broadcastFileOpen(path, content);
          // Trigger syntax highlighting
          if (vm.highlightSyntax) {
            vm.highlightSyntax(tab);
          }
          if (vm.bughostedStatus === 'connected') {
            $timeout(function() { vm.syncEditorState(); }, 50);
          }
        }, function(err) {
          tab.content = '// Error loading file: ' + (err.statusText || 'Unknown error');
          tab.savedContent = '';
          tab.dirty = false;
          tab.lineCount = 1;
          vm.ide.dirty = false;
        });
      };

      vm.onContentChange = function() {
        if (!vm.ide.currentTab) return;
        var isDirty = vm.ide.currentTab.content !== vm.ide.currentTab.savedContent;
        vm.ide.currentTab.dirty = isDirty;
        vm.ide.currentTab.lineCount = (vm.ide.currentTab.content.match(/\n/g) || []).length + 1;
        vm.ide.dirty = isDirty;
        // Trigger syntax highlighting
        if (vm.highlightSyntax) {
          vm.highlightSyntax(vm.ide.currentTab);
        }
        if (_contentSyncDebounce) { $timeout.cancel(_contentSyncDebounce); }
        _contentSyncDebounce = $timeout(function() {
          if (vm.bughostedStatus === 'connected' && vm.bughostedClientId) {
            vm.syncEditorState();
          }
        }, 500, false);
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
          conflictContent: null
        };
        vm.ide.openTabs.push(tab);
        vm.ide.currentFile = fullPath;
        vm.ide.currentTab = tab;
        vm.ide.dirty = true;
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
        vm.ide.openTabs = [];
        vm.ide.currentFile = null;
        vm.ide.currentTab = null;
        vm.ide.dirty = false;
        vm.ide.filePickerPath = '';
        vm.ide.filePickerEntries = [];
        vm.ide.searchFilter = '';
        vm.ide.sharedEditorActive = false;
        vm.ide.sharedFiles = [];
        vm.showIDE = false;
      };

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
