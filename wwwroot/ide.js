'use strict';

angular.module('kanbanApp').factory('IDEMixin', function($http, $timeout) {
  return {
    init: function(vm, $scope) {
      vm.ide = {
        showSidebar: true,
        openTabs: [],
        currentFile: null,
        currentTab: null,
        dirty: false,
        syncing: false,
        filePickerPath: '',
        filePickerEntries: [],
        searchFilter: '',
        lastSavedContent: null,
        pendingFileListing: null,
        pendingFileContent: null
      };

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
        var params = { project: vm.selectedProject };
        if (vm.ide.searchFilter && vm.ide.searchFilter.trim()) {
          params.search = vm.ide.searchFilter.trim();
          if (vm.ide.filePickerPath) {
            params.path = vm.ide.filePickerPath;
          }
        } else if (vm.ide.filePickerPath) {
          params.path = vm.ide.filePickerPath;
        }
        $http.get('/api/editor/list', { params: params }).then(function(resp) {
          vm.ide.filePickerEntries = (resp.data && resp.data.entries) || [];
        }, function() {
          vm.ide.filePickerEntries = [];
        });
      };

      vm.pickerEnterDir = function(path) {
        vm.ide.filePickerPath = path;
        vm.ide.searchFilter = '';
        vm.loadFilePickerEntries();
      };

      vm.pickerUpDir = function() {
        if (!vm.ide.filePickerPath) return;
        var segs = vm.ide.filePickerPath.split('/').filter(function(s) { return s && s.length; });
        segs.pop();
        vm.ide.filePickerPath = segs.join('/');
        vm.loadFilePickerEntries();
      };

      vm.openFile = function(path) {
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
          lineCount: 1
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
          vm.ide.dirty = false;
          vm.ide.lastSavedContent = content;
          vm.broadcastFileOpen(path, content);
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
      };

      vm.saveFile = function() {
        if (!vm.ide.currentFile || !vm.ide.currentTab) return;
        var content = vm.ide.currentTab.content;
        var payload = {
          project: vm.selectedProject,
          path: vm.ide.currentFile,
          content: content
        };
        $http.post('/api/editor/save', payload).then(function() {
          vm.ide.currentTab.savedContent = content;
          vm.ide.currentTab.dirty = false;
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
          lineCount: 1
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
      };

      // Shared editing via BugHosted
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
        if (tab) {
          tab.content = params.content;
          tab.savedContent = params.content;
          tab.dirty = false;
          tab.lineCount = (params.content.match(/\n/g) || []).length + 1;
          if (vm.ide.currentFile === params.path) {
            vm.ide.dirty = false;
          }
        }
        vm.ide.syncing = false;
      };

      vm.startResize = function($event) {
        $event.preventDefault();
        var panel = $event.target.closest('.panel');
        if (!panel) return;
        var startX = $event.clientX;
        var startWidth = panel.offsetWidth;
        function onMouseMove(e) {
          var w = startWidth + (e.clientX - startX);
          if (w > 300) panel.style.width = w + 'px';
        }
        function onMouseUp() {
          document.removeEventListener('mousemove', onMouseMove);
          document.removeEventListener('mouseup', onMouseUp);
        }
        document.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup', onMouseUp);
      };
    }
  };
});
