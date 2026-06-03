'use strict';

angular.module('kanbanApp').factory('IDEMixin', function($http, $timeout) {
  return {
    init: function(vm, $scope) {
      // IDE component state
      vm.ide = {
        show: false,
        files: [],
        currentFile: null,
        content: '',
        path: '',
        isEditing: false,
        filePickerOpen: false,
        filePickerPath: '',
        filePickerEntries: [],
        searchFilter: '',
        selectedFiles: [],
        pickerCardId: null
      };

      // Initialize IDE panel
      vm.initIDE = function() {
        // Create IDE panel element
        const idePanel = document.createElement('div');
        idePanel.className = 'panel ide-panel';
        idePanel.setAttribute('data-panel-type', 'ide');
        idePanel.setAttribute('data-panel-id', 'ide-panel');
        idePanel.innerHTML = `
          <h3>📝 IDE</h3>
          <div class="panel-body">
            <div class="ide-toolbar">
              <button class="small" ng-click="vm.openFilePicker()" title="Open File">📂 Open</button>
              <button class="small" ng-click="vm.saveFile()" ng-disabled="!vm.ide.isEditing" title="Save File">💾 Save</button>
              <button class="small" ng-click="vm.newFile()" title="New File">➕ New</button>
              <button class="small" ng-click="vm.closeIDE()" title="Close IDE">✕ Close</button>
            </div>
            <div class="ide-file-explorer" ng-if="vm.ide.filePickerOpen">
              <div class="file-picker-header">
                <button class="small" ng-click="vm.pickerUpDir()" ng-disabled="!vm.ide.filePickerPath">⬆ Up</button>
                <span>{{vm.ide.filePickerPath || '/'}}</span>
                <button class="small" ng-click="vm.clearSearch()" ng-if="vm.ide.searchFilter" style="margin-left:auto;">✕ Clear</button>
              </div>
              <input type="text" ng-model="vm.ide.searchFilter" placeholder="Search files..." class="search-input" />
              <div class="file-picker-list">
                <div class="entry dir" ng-repeat="e in vm.ide.filePickerEntries" ng-if="e.isDirectory" ng-click="vm.pickerEnterDir(e.path)">
                  <div class="entryTypeAndName"><span>📁</span><span>{{e.name + '/'}}</span></div>
                </div>
                <div class="entry" ng-repeat="e in vm.ide.filePickerEntries" ng-if="!e.isDirectory" ng-click="vm.selectFile(e.path)">
                  <div class="entryTypeAndName"><span>📄</span><span>{{e.name}}</span></div>
                </div>
              </div>
            </div>
            <div class="ide-editor" ng-if="vm.ide.currentFile && !vm.ide.filePickerOpen">
              <div class="editor-header">
                <span class="file-name">{{vm.ide.currentFile}}</span>
                <button class="small" ng-click="vm.closeFile()" title="Close File">✕</button>
              </div>
              <textarea 
                class="editor-textarea" 
                ng-model="vm.ide.content" 
                ng-if="vm.ide.isEditing"
                placeholder="Start editing {{vm.ide.currentFile}}..."
                ng-blur="vm.saveFile()">
              </textarea>
              <div class="editor-view" ng-if="!vm.ide.isEditing">
                <pre>{{vm.ide.content}}</pre>
              </div>
            </div>
            <div class="ide-placeholder" ng-if="!vm.ide.currentFile && !vm.ide.filePickerOpen">
              <p>Click "Open File" to browse and edit files</p>
            </div>
          </div>
        `;
        
        // Insert IDE panel into the right panel
        const rightPanel = document.querySelector('.right-panel');
        if (rightPanel) {
          rightPanel.appendChild(idePanel);
        }
      };

      // Open IDE panel
      vm.openIDE = function() {
        vm.ide.show = true;
        vm.initIDE();
        vm.loadFilePickerEntries();
      };

      // Close IDE panel
      vm.closeIDE = function() {
        vm.ide.show = false;
        vm.ide.currentFile = null;
        vm.ide.content = '';
        vm.ide.isEditing = false;
        vm.ide.filePickerOpen = false;
        vm.ide.filePickerPath = '';
        vm.ide.filePickerEntries = [];
        vm.ide.searchFilter = '';
      };

      // Open file picker
      vm.openFileBrowser = function() {
        vm.ide.filePickerOpen = true;
        vm.loadFilePickerEntries();
      };

      // Close file picker
      vm.closeFilePicker = function() {
        vm.ide.filePickerOpen = false;
        vm.ide.filePickerPath = '';
        vm.ide.filePickerEntries = [];
        vm.ide.searchFilter = '';
      };

      // Load file picker entries
      vm.loadFilePickerEntries = function() {
        const params = { project: vm.selectedProject };
        if (vm.ide.searchFilter && vm.ide.searchFilter.trim()) {
          params.search = vm.ide.searchFilter.trim();
          if (vm.ide.filePickerPath) {
            params.path = vm.ide.filePickerPath;
          }
        } else if (vm.ide.filePickerPath) {
          params.path = vm.ide.filePickerPath;
        }
        
        $http.get('/api/editor/list', { params: params }).then(function (resp) {
          vm.ide.filePickerEntries = (resp.data && resp.data.entries) || [];
        }, function () {
          vm.ide.filePickerEntries = [];
        });
      };

      // Enter directory in file picker
      vm.pickerEnterDir = function(path) {
        vm.ide.filePickerPath = path;
        vm.loadFilePickerEntries();
      };

      // Go up directory in file picker
      vm.pickerUpDir = function() {
        if (!vm.ide.filePickerPath) return;
        const segs = vm.ide.filePickerPath.split('/').filter(function (s) { return s && s.length; });
        segs.pop();
        vm.ide.filePickerPath = segs.join('/');
        vm.loadFilePickerEntries();
      };

      // Select file for editing
      vm.selectFile = function(path) {
        vm.ide.currentFile = path;
        vm.ide.filePickerOpen = false;
        vm.loadFileContent(path);
      };

      // Load file content
      vm.loadFileContent = function(path) {
        $http.get('/api/editor/content', { params: { path: path } }).then(function (resp) {
          vm.ide.content = resp.data || '';
          vm.ide.isEditing = true;
        }, function () {
          vm.ide.content = '';
          vm.ide.isEditing = true;
        });
      };

      // Save file
      vm.saveFile = function() {
        if (!vm.ide.currentFile || !vm.ide.isEditing) return;
        
        const payload = {
          path: vm.ide.currentFile,
          content: vm.ide.content
        };
        
        $http.post('/api/editor/save', payload).then(function () {
          // File saved successfully
        }, function (err) {
          console.error('Failed to save file:', err);
        });
      };

      // Create new file
      vm.newFile = function() {
        const fileName = prompt('Enter new file name:');
        if (fileName) {
          const fullPath = vm.ide.filePickerPath ? vm.ide.filePickerPath + '/' + fileName : fileName;
          vm.ide.currentFile = fullPath;
          vm.ide.content = '';
          vm.ide.isEditing = true;
          vm.ide.filePickerOpen = false;
        }
      };

      // Close current file
      vm.closeFile = function() {
        vm.ide.currentFile = null;
        vm.ide.content = '';
        vm.ide.isEditing = false;
      };

      // Clear search
      vm.clearSearch = function() {
        vm.ide.searchFilter = '';
        vm.loadFilePickerEntries();
      };

      // Debounce function for search
      function debounce(func, wait) {
        let timeout;
        return function executedFunction(...args) {
          const later = () => {
            clearTimeout(timeout);
            func(...args);
          };
          clearTimeout(timeout);
          timeout = setTimeout(later, wait);
        };
      }

      // Debounced search
      vm.debouncedSearch = debounce(function() {
        vm.loadFilePickerEntries();
      }, 300);

      // Watch search filter changes
      $scope.$watch('vm.ide.searchFilter', function(newVal, oldVal) {
        if (newVal !== oldVal) {
          vm.debouncedSearch();
        }
      });

      // Initialize IDE when app loads
      vm.initIDE();
    }
  };
});