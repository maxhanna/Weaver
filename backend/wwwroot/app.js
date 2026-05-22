angular.module('kanbanApp', [])
  .controller('MainCtrl', ['$http', '$interval', '$window', '$scope', function($http, $interval, $window, $scope) {
    const vm = this;
    const STORAGE_KEY = 'kanban.cards';

    vm.aiPrompt = '';
    vm.aiResponse = '';
    vm.termInput = '';
    vm.terminalOutput = '';
    vm.streamingThinking = '';
    vm.streamingEdits = [];
    vm.streamingCommands = [];
    vm.streamingSummary = '';
    vm.streamingActive = false;

    // === Project configuration ===

    vm.projects = [];
    vm.selectedProject = '';

    function normalizeProjects(raw) {
      return raw.map(function(p) { return { Name: p.Name || p.name, Path: p.Path || p.path, Description: p.Description || p.description || '' }; });
    }

    vm.loadConfig = function() {
      $http.get('/api/config').then(function(resp) {
        var cfg = resp.data || {};
        var raw = (cfg.projects && cfg.projects.length) ? cfg.projects : [
          { Name: 'Project Alpha', Path: '../project-alpha' },
          { Name: 'Project Beta', Path: '../project-beta' }
        ];
        vm.projects = normalizeProjects(raw);
        vm.selectedProject = cfg.defaultProject || (vm.projects.length ? vm.projects[0].Path : '');
        vm.defaultProject = cfg.defaultProject;
      }, function() {
        vm.projects = normalizeProjects([
          { Name: 'Project Alpha', Path: '../project-alpha' },
          { Name: 'Project Beta', Path: '../project-beta' }
        ]);
        vm.selectedProject = vm.projects[0].Path;
        vm.defaultProject = vm.projects[0].Path;
      });
    };

    vm.loadConfig();

    // UI controls for project options menu
    vm.showProjectOptions = false;
    vm.toggleProjectOptions = function() { vm.showProjectOptions = !vm.showProjectOptions; };

    vm.getSelectedProjectDescription = function() {
      if (!vm.selectedProject) return '';
      var proj = vm.projects.find(function(p) { return (p.Path || p.path) === vm.selectedProject; });
      return proj ? (proj.Description || proj.description || '') : '';
    };

    vm.showAddProjectPanel = false;
    vm.newProjectName = '';
    vm.newProjectPath = '';
    vm.newProjectDescription = '';
    vm.editMode = false;
    vm.editingProjectPath = '';

    vm.addProjectUI = function() {
      vm.showAddProjectPanel = true;
      vm.newProjectName = '';
      vm.newProjectPath = '';
    };

    vm.closeAddProjectPanel = function() {
      vm.showAddProjectPanel = false;
    };

    vm.addProjectFromPanel = function() {
      if (!vm.newProjectName) {
        $window.alert('Project name is required');
        return;
      }
      if (!vm.newProjectPath) {
        $window.alert('Project path is required');
        return;
      }
      var payload = { Name: vm.newProjectName, Path: vm.newProjectPath.replace(/\\/g, "/"), Description: vm.newProjectDescription || '' };
      
      $http.post('/api/config/projects/add', payload).then(function(resp) {
        vm.loadConfig();
        vm.closeAddProjectPanel();
      }, function(err) {
        $window.alert('Failed to add project: ' + (err.data || err.statusText || err));
      });
    };

    vm.editProjectUI = function() {
      if (!vm.selectedProject) return $window.alert('No project selected');
      var proj = vm.projects.find(function(p) { return (p.Path || p.path) === vm.selectedProject; });
      if (!proj) return $window.alert('Selected project not found');
      vm.showAddProjectPanel = true;
      vm.newProjectName = proj.Name || proj.name || '';
      vm.newProjectPath = proj.Path || proj.path || '';
      vm.newProjectDescription = proj.Description || proj.description || '';
      vm.editMode = true;
      vm.editingProjectPath = proj.Path || proj.path || '';
    };

    vm.updateProjectFromPanel = function() {
      if (!vm.editMode) return vm.addProjectFromPanel();
      if (!vm.newProjectName || !vm.newProjectPath) return $window.alert('Name and Path are required');

      // Load current config, update the matching project, then save entire config
      $http.get('/api/config').then(function(resp) {
        var cfg = resp.data || { projects: [] };
        cfg.projects = cfg.projects || [];
        var idx = cfg.projects.findIndex(function(p) { return (p.Path || p.path) === vm.editingProjectPath; });
        if (idx === -1) return $window.alert('Original project not found in config');

        // Prevent duplicate paths if changed
        var newPath = vm.newProjectPath.replace(/\\/g, "/");
        if (newPath !== vm.editingProjectPath && cfg.projects.some(function(p){ return (p.Path || p.path) === newPath; })) {
          return $window.alert('A project with that path already exists');
        }

        cfg.projects[idx].Name = vm.newProjectName;
        cfg.projects[idx].Path = newPath;
        cfg.projects[idx].Description = vm.newProjectDescription || '';

        // Persist
        $http.post('/api/config/save', cfg).then(function() {
          vm.loadConfig();
          vm.closeAddProjectPanel();
          vm.editMode = false;
          vm.editingProjectPath = '';
        }, function(err) {
          $window.alert('Failed to save config: ' + (err.data || err.statusText || err));
        });
      }, function(err) { $window.alert('Failed to load config: ' + (err.data || err.statusText || err)); });
    };

    vm.removeProjectUI = function() {
      if(!vm.selectedProject) return $window.alert('No project selected');
      var proj = vm.projects.find(function(p) { return (p.Path || p.path) === vm.selectedProject; });
      if(!proj) return;
      if(!$window.confirm('Remove project "' + (proj.Name || proj.name) + '" (' + vm.selectedProject + ')?')) return;
      $http.post('/api/config/projects/remove', { Path: vm.selectedProject }).then(function() {
        vm.loadConfig();
      }, function(err) {
        $window.alert('Failed to remove project: ' + (err.data || err.statusText || err));
      });
    };

    vm.setDefaultProject = function() {
      if(!vm.selectedProject) return $window.alert('No project selected');
      $http.post('/api/config/default-project', { ProjectPath: vm.selectedProject }).then(function() {
        vm.defaultProject = vm.selectedProject;
        $window.alert('Default project set successfully');
      }, function(err) {
        $window.alert('Failed to set default project: ' + (err.data || err.statusText || err));
      });
    };

    // === Card state ===

    function uid() { return Math.random().toString(36).slice(2,9); }

    function loadCards(){
      var raw = $window.localStorage.getItem(STORAGE_KEY);
      return raw ? JSON.parse(raw) : { todo:[], doing:[], done:[] };
    }

    function saveCards(){ $window.localStorage.setItem(STORAGE_KEY, JSON.stringify(vm.state)); }

    vm.state = loadCards();

    // Filter cards by the selected project
    vm.cardsForProject = function(col) {
      var all = vm.state[col] || [];
      if (!vm.selectedProject) return all;
      return all.filter(function(c) { return c.filePath === vm.selectedProject; });
    };

    vm.changeProject = function() {
      console.log('Changed project to:', vm.selectedProject);
    };

    vm.addCard = function(col){
      var text = $window.prompt('Card text'); if(!text) return;
      vm.state[col].push({ id: uid(), text: text, filePath: vm.selectedProject }); saveCards();
    };

    vm.moveCard = function(id, from, to){
      var idx = vm.state[from].findIndex(function(c){ return c.id === id; }); if(idx === -1) return;
      var card = vm.state[from].splice(idx,1)[0]; 
      vm.state[to].push(card); 
      saveCards();
    };

    vm.startCard = function(card) {
      if (!card) return;
      
      // Execute AI on the card first
      vm.executeAgent(card);
      
      // Then move to 'doing' column (this will happen after AI execution completes)
      // The moveCard call is already included in executeAgent function
    };

    vm.selectCard = function(card){
      vm.aiPrompt = card.text;
    };

    vm.editCardText = function(card) {
      var newText = $window.prompt('Edit task text:', card.text);
      if (newText !== null && newText !== card.text) {
        card.text = newText;
        saveCards();
      }
    };

    vm.getAttachedFiles = function(card) {
      if (Array.isArray(card.attached)) return card.attached;
      if (card.attached) return [card.attached];
      return [];
    };

    vm.removeAttachment = function(cardId) {
      ['todo','doing','done'].forEach(function(col) {
        var cards = vm.state[col];
        for (var i = 0; i < cards.length; i++) {
          if (cards[i].id === cardId) {
            delete cards[i].attached;
            delete cards[i].attachedProject;
            break;
          }
        }
      });
      saveCards();
    };

    // === File picker for card attachments ===

    vm.showFilePicker = false;
    vm.pickerCardId = null;
    vm.pickerPath = '';
    vm.pickerEntries = [];
    vm.pickerSelected = [];

    vm.attachFile = function(cardId) {
      vm.pickerCardId = cardId;
      vm.pickerPath = '';
      vm.pickerSelected = [];
      vm.showFilePicker = true;
      vm.loadPickerEntries();
    };

    vm.closeFilePicker = function() {
      vm.showFilePicker = false;
      vm.pickerCardId = null;
      vm.pickerPath = '';
      vm.pickerEntries = [];
      vm.pickerSelected = [];
    };

    vm.loadPickerEntries = function() {
      var params = { project: vm.selectedProject };
      if (vm.pickerPath) params.path = vm.pickerPath;
      $http.get('/api/editor/list', { params: params }).then(function(resp) {
        var data = resp.data || {};
        vm.pickerEntries = data.entries || [];
      }, function() {
        vm.pickerEntries = [];
      });
    };

    vm.pickerEnterDir = function(path) {
      vm.pickerPath = path;
      vm.loadPickerEntries();
    };

    vm.pickerUpDir = function() {
      if (!vm.pickerPath) return;
      var segs = vm.pickerPath.split('/').filter(function(s){ return s && s.length; });
      segs.pop();
      vm.pickerPath = segs.join('/');
      vm.loadPickerEntries();
    };

    vm.pickerSelectFile = function(path) {
      var idx = vm.pickerSelected.indexOf(path);
      if (idx === -1) {
        vm.pickerSelected.push(path);
      } else {
        vm.pickerSelected.splice(idx, 1);
      }
    };

    vm.confirmFilePicker = function() {
      if (!vm.pickerSelected.length) { $window.alert('Please select at least one file.'); return; }
      var cardId = vm.pickerCardId;
      if (!cardId) { vm.closeFilePicker(); return; }
      // Find the card and store the attached file paths
      var found = false;
      ['todo','doing','done'].forEach(function(col) {
        var cards = vm.state[col];
        for (var i = 0; i < cards.length; i++) {
          if (cards[i].id === cardId) {
            cards[i].attached = angular.copy(vm.pickerSelected);
            cards[i].attachedProject = vm.selectedProject;
            found = true;
            break;
          }
        }
      });
      if (found) saveCards();
      vm.closeFilePicker();
    };

    // === AI Agent (streaming) ===

    vm.agentResult = null;

    vm.executeAgent = function(card) {
      if (!card) return;
      if (!card.text) { $window.alert('Card has no task text'); return; }
      var proj = card.filePath || vm.selectedProject;
      if (!proj) { $window.alert('No project assigned to this card'); return; }

      // Reset streaming state
      vm.agentResult = null;
      vm.aiResponse = '';
      vm.streamingThinking = '';
      vm.streamingEdits = [];
      vm.streamingCommands = [];
      vm.streamingSummary = '';
      vm.streamingActive = true;
      vm.aiPrompt = card.text;

      var files = card.attached || [];
      var payload = { prompt: card.text, project: proj, files: files };

      // Mark the card as in-progress now that execution started
      moveCardToDoing(card.id);

      // Use fetch with streaming reader
      fetch('/api/agent/execute-stream', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      }).then(function(response) {
        if (!response.ok) {
          vm.streamingActive = false;
          vm.agentResult = { error: 'Server error: ' + response.status };
          $scope.$digest();
          return;
        }
        var reader = response.body.getReader();
        var decoder = new TextDecoder();
        var buffer = '';

        function readNext() {
          reader.read().then(function(result) {
            if (result.done) {
              vm.streamingActive = false;
              $scope.$digest();
              return;
            }
            buffer += decoder.decode(result.value, { stream: true });

            // Parse SSE events from buffer
            var parts = buffer.split('\n\n');
            buffer = parts.pop();

            for (var p = 0; p < parts.length; p++) {
              var block = parts[p];
              var lines = block.split('\n');
              var eventName = '';
              var data = '';

              for (var l = 0; l < lines.length; l++) {
                if (lines[l].startsWith('event: ')) eventName = lines[l].substring(7);
                else if (lines[l].startsWith('data: ')) data += lines[l].substring(6);
              }

              if (eventName) {
                var parsed = null;
                try { parsed = JSON.parse(data); } catch(e) {}

                switch (eventName) {
                  case 'token':
                    if (parsed && parsed.t) vm.streamingThinking += parsed.t;
                    break;
                  case 'edit':
                    if (parsed) vm.streamingEdits.push(parsed);
                    break;
                  case 'command':
                    if (parsed) vm.streamingCommands.push(parsed);
                    break;
                  case 'summary':
                    if (parsed) vm.streamingSummary = parsed.summary || '';
                    break;
                  case 'done':
                    vm.streamingActive = false;
                    vm.agentResult = { summary: vm.streamingSummary, thinking: vm.streamingThinking };
                    vm.aiResponse = vm.streamingSummary || 'Agent completed.';
                    moveCardToDone(card.id);
                    break;
                  case 'error':
                    vm.streamingActive = false;
                    vm.agentResult = { error: parsed ? parsed.message : data };
                    vm.aiResponse = 'Error: ' + (parsed ? parsed.message : data);
                    break;
                }
              }
            }

            $scope.$digest();
            readNext();
          });
        }

        readNext();
      }).catch(function(err) {
        vm.streamingActive = false;
        vm.agentResult = { error: 'Connection failed: ' + err.message };
        $scope.$digest();
      });
    };

    function moveCardToDoing(cardId) {
      var idx = vm.state.todo.findIndex(function(c){ return c.id === cardId; });
      if (idx === -1) return;
      var card = vm.state.todo.splice(idx, 1)[0];
      vm.state.doing.push(card);
      saveCards();
    }

    function moveCardToDone(cardId) {
      var idx = vm.state.doing.findIndex(function(c){ return c.id === cardId; });
      if (idx === -1) return;
      var card = vm.state.doing.splice(idx, 1)[0];
      vm.state.done.push(card);
      saveCards();
    }

    // === AI Chat ===

    vm.askAI = function(){
      if(!vm.aiPrompt) return $window.alert('Enter a prompt');
      vm.agentResult = null;
      vm.aiResponse = 'Thinking...';
      $http.post('/api/ai/generate', { prompt: vm.aiPrompt })
        .then(function(resp){
          if(typeof resp.data === 'string') vm.aiResponse = resp.data;
          else vm.aiResponse = JSON.stringify(resp.data, null, 2);
        }, function(err){ vm.aiResponse = 'Error: ' + (err.statusText || err); });
    };

    // === Terminal ===

    vm.startTerminal = function(){ $http.post('/api/terminal/start').catch(function(){}); };

    vm.sendCmd = function(){ if(!vm.termInput) return; $http.post('/api/terminal/exec', { command: vm.termInput }).then(function(){ vm.termInput = ''; vm.refreshTerminal(); }); };

    vm.refreshTerminal = function(){
      $http.get('/api/terminal/output').then(function(resp){ vm.terminalOutput = resp.data.output || ''; });
    };

    vm.refreshTerminal();

    // Initialize column resizers so users can drag to resize Kanban columns
    vm.initColumnResizers = function() {
      try {
        // remove any existing resizers
        var existing = document.querySelectorAll('.col-resizer');
        existing.forEach(function(el){ el.remove(); });

        var cols = Array.prototype.slice.call(document.querySelectorAll('#board .column'));
        for (var i = 0; i < cols.length - 1; i++) {
          (function(leftCol){
            var resizer = document.createElement('div');
            resizer.className = 'col-resizer';
            leftCol.appendChild(resizer);

            resizer.addEventListener('pointerdown', function startDrag(e) {
              e.preventDefault();
              var rightCol = leftCol.nextElementSibling;
              if (!rightCol) return;
              var startX = e.clientX;
              var leftRect = leftCol.getBoundingClientRect();
              var rightRect = rightCol.getBoundingClientRect();
              var leftWidth = leftRect.width;
              var rightWidth = rightRect.width;
              var min = 160; // minimum column width
              document.body.style.userSelect = 'none';
              resizer.classList.add('active');

              function onMove(ev) {
                var dx = ev.clientX - startX;
                var newLeft = leftWidth + dx;
                var newRight = rightWidth - dx;
                var total = leftWidth + rightWidth;
                if (newLeft < min) { newLeft = min; newRight = total - min; }
                if (newRight < min) { newRight = min; newLeft = total - min; }
                leftCol.style.flex = '0 0 ' + Math.round(newLeft) + 'px';
                rightCol.style.flex = '0 0 ' + Math.round(newRight) + 'px';
              }

              function stopDrag() {
                document.removeEventListener('pointermove', onMove);
                document.removeEventListener('pointerup', stopDrag);
                document.body.style.userSelect = '';
                resizer.classList.remove('active');
              }

              document.addEventListener('pointermove', onMove);
              document.addEventListener('pointerup', stopDrag);
            });

            // double-click resets flex sizing to allow automatic layout
            resizer.addEventListener('dblclick', function() {
              cols.forEach(function(c){ c.style.flex = ''; c.style.width = ''; });
            });
          })(cols[i]);
        }
      } catch (err) {
        console.error('initColumnResizers error', err);
      }
    };

    // run after a short delay so DOM is ready
    setTimeout(function(){ vm.initColumnResizers(); }, 200);

    $interval(vm.refreshTerminal, 2000);
  }]);
