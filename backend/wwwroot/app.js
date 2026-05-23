angular.module('kanbanApp', [])
  .controller('MainCtrl', ['$http', '$interval', '$window', '$scope', '$timeout', function ($http, $interval, $window, $scope, $timeout) {
    const vm = this;
    const STORAGE_KEY = 'maestroconfig.cards';
    const SETTINGS_KEY = 'maestroconfig.settings';

    // === State ===
    vm.selectedProject = '';
    vm.projects = [];
    vm.defaultProject = '';
    vm.aiPrompt = '';
    vm.aiResponse = '';
    vm.terminalOutput = '';
    vm.termInput = '';
    vm.selectedCardId = null;
    vm.activeCardText = '';
    vm.autoQueue = true;
    vm.showTerminal = true;

    // Agent streaming state
    vm.streamingActive = false;
    vm.streamingThinking = '';
    vm.streamingSummary = '';
    vm.streamingPhase = '';
    vm.streamingSteps = [];
    vm.streamingFilesEdited = [];
    vm.agentActivityLog = [];
    vm.activeStepIndex = null;
    vm.lastPhaseLogged = '';
    vm.agentResult = null;
    vm.abortController = null;
    vm.lastStreamingSteps = [];
    vm.lastStreamingPhase = '';
    vm.lastStreamingSummary = '';
    vm.lastStreamingThinking = '';
    vm.streamingStepsCopy = [];

    // Debug logging for file size and token count
    vm.logFileSizeAndTokens = function (filePath, content) {
      if (!filePath || !content) return;
      const fileSize = content.length;
      const tokenCount = Math.ceil(fileSize / 4); // Rough estimate
      vm.addLogEntry({
        type: 'debug',
        message: `File: ${filePath} | Size: ${fileSize} chars | Tokens: ~${tokenCount}`
      });
    };

    // Scroll to bottom of agent log
    vm.scrollToBottom = function () {
      $timeout(function () {
        var logContainer = document.querySelector('.agent-log');
        if (logContainer) {
          logContainer.scrollTop = logContainer.scrollHeight;
        }
      }, 0);
    };

    // Auto-scroll agent log when new content is added
    vm.addLogEntry = function (entry) {
      // Debounce log entries to prevent digest loop errors
      if (vm.lastLogEntry && vm.lastLogEntry.message === entry.message) {
        return;
      }
      vm.lastLogEntry = entry;
      vm.agentActivityLog.push(entry);
      vm.scrollToBottom();
    };

    // Project UI
    vm.showProjectOptions = false;
    vm.showAddProjectPanel = false;
    vm.showSettingsPanel = false;
    vm.showFilePicker = false;
    vm.editMode = false;
    vm.newProjectName = '';
    vm.newProjectPath = '';
    vm.newProjectDescription = '';
    vm.editingProjectPath = '';

    // File picker
    vm.pickerCardId = null;
    vm.pickerPath = '';
    vm.pickerEntries = [];
    vm.pickerSelected = [];

    // Settings
    vm.settingsDefaultProject = '';

    // === Load settings from localStorage ===
    function loadSettings() {
      try {
        var raw = $window.localStorage.getItem(SETTINGS_KEY);
        if (raw) {
          var s = JSON.parse(raw);
          vm.autoQueue = s.autoQueue !== false;
        }
      } catch (e) { }
    }
    loadSettings();

    function saveSettings() {
      try {
        $window.localStorage.setItem(SETTINGS_KEY, JSON.stringify({ autoQueue: vm.autoQueue }));
      } catch (e) { }
    }

    vm.saveSettings = function () {
      saveSettings();
      $http.get('/api/config').then(function (resp) {
        var cfg = resp.data || { projects: vm.projects };
        cfg.projects = cfg.projects || vm.projects;
        cfg.defaultProject = vm.settingsDefaultProject || cfg.defaultProject || vm.defaultProject;
        cfg.showTerminal = vm.showTerminal !== false;
        return $http.post('/api/config/save', cfg);
      }).then(function () {
        vm.defaultProject = vm.settingsDefaultProject || vm.defaultProject;
        if (vm.settingsDefaultProject) vm.selectedProject = vm.settingsDefaultProject;
        vm.loadConfig();
        vm.closeSettingsPanel();
      }, function (err) {
        $window.alert('Failed to save settings: ' + (err.data || err.statusText || err));
      });
    };

    // === Project config ===
    function normalizeProjects(raw) {
      return raw.map(function (p) { return { Name: p.Name || p.name, Path: p.Path || p.path, Description: p.Description || p.description || '' }; });
    }

    vm.loadConfig = function () {
      $http.get('/api/config').then(function (resp) {
        var cfg = resp.data || {};
        var raw = (cfg.projects && cfg.projects.length) ? cfg.projects : [
          { Name: 'Project Alpha', Path: '../project-alpha' }
        ];
        vm.projects = normalizeProjects(raw);
        vm.selectedProject = cfg.defaultProject || (vm.projects.length ? vm.projects[0].Path : '');
        vm.defaultProject = cfg.defaultProject;
        if (typeof cfg.showTerminal === 'boolean') vm.showTerminal = cfg.showTerminal;
      }, function () {
        vm.projects = normalizeProjects([{ Name: 'Default', Path: '..' }]);
        vm.selectedProject = '..';
        vm.defaultProject = '..';
      });
    };
    vm.loadConfig();

    vm.getSelectedProjectDescription = function () {
      if (!vm.selectedProject) return '';
      var p = vm.projects.find(function (p) { return (p.Path || p.path) === vm.selectedProject; });
      return p ? (p.Description || '') : '';
    };

    vm.toggleProjectOptions = function () { vm.showProjectOptions = !vm.showProjectOptions; };
    vm.changeProject = function () { };

    vm.addProjectUI = function () {
      vm.showAddProjectPanel = true;
      vm.editMode = false;
      vm.newProjectName = '';
      vm.newProjectPath = '';
      vm.newProjectDescription = '';
    };

    vm.closeAddProjectPanel = function () { vm.showAddProjectPanel = false; };

    vm.addProjectFromPanel = function () {
      if (!vm.newProjectName) return $window.alert('Project name is required');
      if (!vm.newProjectPath) return $window.alert('Project path is required');
      $http.post('/api/config/projects/add', {
        Name: vm.newProjectName,
        Path: vm.newProjectPath.replace(/\\/g, '/'),
        Description: vm.newProjectDescription || ''
      }).then(function () {
        vm.loadConfig();
        vm.closeAddProjectPanel();
      }, function (err) {
        $window.alert('Failed to add project: ' + (err.data || err.statusText));
      });
    };

    vm.editProjectUI = function () {
      if (!vm.selectedProject) return $window.alert('No project selected');
      var p = vm.projects.find(function (p) { return (p.Path || p.path) === vm.selectedProject; });
      if (!p) return;
      vm.showAddProjectPanel = true;
      vm.editMode = true;
      vm.newProjectName = p.Name || '';
      vm.newProjectPath = p.Path || '';
      vm.newProjectDescription = p.Description || '';
      vm.editingProjectPath = p.Path || '';
    };

    vm.updateProjectFromPanel = function () {
      if (!vm.editMode) return vm.addProjectFromPanel();
      if (!vm.newProjectName || !vm.newProjectPath) return $window.alert('Name and Path are required');
      $http.get('/api/config').then(function (resp) {
        var cfg = resp.data || { projects: [] };
        cfg.projects = cfg.projects || [];
        var idx = cfg.projects.findIndex(function (p) { return (p.Path || p.path) === vm.editingProjectPath; });
        if (idx === -1) return $window.alert('Original project not found');
        var newPath = vm.newProjectPath.replace(/\\/g, '/');
        if (newPath !== vm.editingProjectPath && cfg.projects.some(function (p) { return (p.Path || p.path) === newPath; }))
          return $window.alert('A project with that path already exists');
        cfg.projects[idx].Name = vm.newProjectName;
        cfg.projects[idx].Path = newPath;
        cfg.projects[idx].Description = vm.newProjectDescription || '';
        $http.post('/api/config/save', cfg).then(function () {
          vm.loadConfig();
          vm.closeAddProjectPanel();
          vm.editMode = false;
          vm.editingProjectPath = '';
        }, function (err) { $window.alert('Failed to save: ' + (err.data || err.statusText)); });
      });
    };

    vm.removeProjectUI = function () {
      if (!vm.selectedProject) return $window.alert('No project selected');
      var p = vm.projects.find(function (p) { return (p.Path || p.path) === vm.selectedProject; });
      if (!p) return;
      if (!$window.confirm('Remove project "' + (p.Name || '') + '" (' + vm.selectedProject + ')?')) return;
      $http.post('/api/config/projects/remove', { Path: vm.selectedProject }).then(function () { vm.loadConfig(); });
    };

    vm.openSettingsPanel = function () {
      vm.settingsDefaultProject = vm.defaultProject || vm.selectedProject;
      vm.showSettingsPanel = true;
      // Show backdrop when settings panel is opened
      var backdrop = document.getElementById('backdrop');
      if (backdrop) {
        backdrop.style.display = 'block';
      }
    };
    vm.closeSettingsPanel = function () { 
      vm.showSettingsPanel = false; 
      // Hide backdrop when settings panel is closed
      var backdrop = document.getElementById('backdrop');
      if (backdrop) {
        backdrop.style.display = 'none';
      }
    };

    // === Cards ===
    function uid() { return Math.random().toString(36).slice(2, 9); }

    function loadCards() {
      var raw = $window.localStorage.getItem(STORAGE_KEY);
      return raw ? JSON.parse(raw) : { todo: [], doing: [], done: [] };
    }
    function saveCards() { $window.localStorage.setItem(STORAGE_KEY, JSON.stringify(vm.state)); }

    vm.state = loadCards();

    vm.cardsForProject = function (col) {
      var all = vm.state[col] || [];
      if (!vm.selectedProject) return all;
      return all.filter(function (c) { return c.filePath === vm.selectedProject; });
    };

    vm.addCard = function (col) {
      vm.state[col].push({
        id: uid(),
        text: '',
        filePath: vm.selectedProject,
        createdAt: new Date().toISOString(),
        priority: 'medium',
        attached: []
      });
      saveCards();
      $timeout(function () {
        var newCard = vm.state[col][vm.state[col].length - 1];
        var textarea = document.querySelector('[data-card-id="' + newCard.id + '"] textarea');
        if (textarea) textarea.focus();
      }, 0);
    };

    vm.clearDoneCards = function () {
      if (!$window.confirm('Delete all done tasks?')) return;
      vm.state.done = [];
      saveCards();
    };

    vm.openDeleteCardConfirm = function (id, col) {
      vm.confirmDeleteCardId = id;
      var col = col || 'done';
      var card = vm.state[col].find(function (c) { return c.id === id; });
      if (!card) {
        alert('Card not found in ' + col + ' column');
        return;
      }
      vm.deleteCardConfirm = {
        id: id,
        col: col,
        show: true,
        dontShowAgain: false
      };
      // Ensure modal is visible
      const modal = document.querySelector('.delete-confirm-modal');
      if (modal) {
        modal.style.display = 'flex';
        // Ensure backdrop is properly applied
        modal.style.backdropFilter = 'blur(4px)'; 
      }
    };

    vm.confirmDeleteCard = function () {
      if (!vm.deleteCardConfirm || !vm.deleteCardConfirm.id) return;
      var id = vm.deleteCardConfirm.id;
      var col = vm.deleteCardConfirm.col;
      var idx = vm.state[col].findIndex(function (c) { return c.id === id; });
      if (idx !== -1) {
        vm.state[col].splice(idx, 1);
        console.log('Deleted card with id', id);
        saveCards();
      }
      if (vm.deleteCardConfirm.dontShowAgain) {
        try {
          $window.localStorage.setItem('maestroconfig.deleteCardConfirm', 'false');
        } catch (e) { }
      }
      vm.confirmDeleteCardId = null;
      vm.deleteCardConfirm = null;
    };

    vm.closeDeleteCardConfirm = function(event) {
      // Handle ESC key press directly
      if (event && event.key === 'Escape') {
        event.stopPropagation();
        event.preventDefault();
        vm.confirmDeleteCardId = null;
        vm.deleteCardConfirm = null; 
        const modal = document.querySelector('.delete-confirm-modal');
        if (modal) {
          modal.style.display = 'none';
        }
       // $scope.$evalAsync();
        return;
      }
      vm.confirmDeleteCardId = null;
      vm.deleteCardConfirm = null;
    };
 

    // Drag and drop functionality
    vm.dragStart = function (event, card, col) {
      event.dataTransfer.setData('text/plain', JSON.stringify({ card, col }));
    };

    vm.dragOver = function (event) {
      event.preventDefault();
    };

    vm.drop = function (event, targetCol) {
      event.preventDefault();
      var data = event.dataTransfer.getData('text/plain');
      if (!data) return;

      var { card, col } = JSON.parse(data);

      // Remove card from source column
      var sourceCol = vm.state[col];
      var cardIndex = sourceCol.findIndex(c => c.id === card.id);
      if (cardIndex !== -1) {
        sourceCol.splice(cardIndex, 1);

        // Add card to target column
        vm.state[targetCol].push(card);

        // Save updated state
        saveCards();
      }
    };

 
    document.addEventListener('keydown', function (event) {
      if (event.key === 'Escape' && vm.deleteCardConfirm && vm.deleteCardConfirm.show) {
       console.log('ESC pressed - closing delete confirmation');
        vm.closeSettingsPanel();
        vm.closeAddProjectPanel();
        vm.closeFilePicker();
        vm.closeDeleteCardConfirm();
      }
    });

    vm.selectCard = function (card) {
      vm.selectedCardId = card.id;
      vm.aiPrompt = card.text;
    };

    vm.editCardText = function (card) {
      var newText = $window.prompt('Edit task:', card.text);
      if (newText !== null && newText !== card.text) {
        card.text = newText;
        saveCards();
      }
    };

    vm.saveCardText = function (card) {
      saveCards();
    };

    vm.moveCard = function (id, from, to) {
      var idx = vm.state[from].findIndex(function (c) { return c.id === id; });
      if (idx === -1) return;
      var card = vm.state[from][idx];
      if (from === 'todo' && to === 'doing' && !card.ready) {
        return $window.alert('Mark the card as Ready first (press Start)');
      }
      vm.state[from].splice(idx, 1);
      if (from === 'doing' && to === 'todo') {
        card.ready = false;
        delete card.agentAnalysis;
      }
      vm.state[to].push(card);
      if (from === 'todo' && to === 'doing' && card.ready) {
        vm.executeAgent(card);
      }
      saveCards();
    };

    vm.reopenCard = function (card) {
      // Clear analysis, move to todo
      card.ready = false;
      delete card.agentAnalysis;
      delete card.agentSteps;
      var idx = vm.state.done.findIndex(function (c) { return c.id === card.id; });
      if (idx === -1) return;
      vm.state.done.splice(idx, 1);
      vm.state.todo.push(card);
      saveCards();
    };

    // === Attachments ===
    vm.getAttachedFiles = function (card) {
      if (Array.isArray(card.attached)) return card.attached;
      if (card.attached) return [card.attached];
      return [];
    };

    vm.removeAttachment = function (cardId) {
      ['todo', 'doing', 'done'].forEach(function (col) {
        var cards = vm.state[col];
        for (var i = 0; i < cards.length; i++) {
          if (cards[i].id === cardId) {
            cards[i].attached = [];
            break;
          }
        }
      });
      saveCards();
    };

    vm.pickerToggleFile = function (path) {
      var idx = vm.pickerSelected.indexOf(path);
      if (idx === -1) vm.pickerSelected.push(path);
      else vm.pickerSelected.splice(idx, 1);
    };

    vm.attachFile = function (cardId) {
      vm.pickerCardId = cardId;
      vm.pickerPath = '';
      vm.pickerSelected = [];
      vm.showFilePicker = true;
      vm.loadPickerEntries();
    };

    vm.closeFilePicker = function () {
      vm.showFilePicker = false;
      vm.pickerCardId = null;
      vm.pickerPath = '';
      vm.pickerEntries = [];
      vm.pickerSelected = [];
    };

    vm.loadPickerEntries = function () {
      var params = { project: vm.selectedProject };
      if (vm.pickerPath) params.path = vm.pickerPath;
      $http.get('/api/editor/list', { params: params }).then(function (resp) {
        vm.pickerEntries = (resp.data && resp.data.entries) || [];
      }, function () { vm.pickerEntries = []; });
    };

    vm.pickerEnterDir = function (path) { vm.pickerPath = path; vm.loadPickerEntries(); };

    vm.pickerUpDir = function () {
      if (!vm.pickerPath) return;
      var segs = vm.pickerPath.split('/').filter(function (s) { return s && s.length; });
      segs.pop();
      vm.pickerPath = segs.join('/');
      vm.loadPickerEntries();
    };

    vm.confirmFilePicker = function () {
      if (!vm.pickerSelected.length) return $window.alert('Select at least one file');
      var cardId = vm.pickerCardId;
      if (!cardId) return vm.closeFilePicker();
      ['todo', 'doing', 'done'].forEach(function (col) {
        var cards = vm.state[col];
        for (var i = 0; i < cards.length; i++) {
          if (cards[i].id === cardId) {
            cards[i].attached = angular.copy(vm.pickerSelected);
            cards[i].attachedProject = vm.selectedProject;
            break;
          }
        }
      });
      saveCards();
      vm.closeFilePicker();
    };

    // === Agent helpers ===
    function normalizeStepStatus(status) {
      if (status === 'written' || status === 'ok' || status === 'created' || status === 'modified') return 'done';
      if (status === 'running' || status === 'error' || status === 'pending') return status;
      return status || 'pending';
    }

    function normalizeStep(step) {
      if (!step) return step;
      step.status = normalizeStepStatus(step.status);
      return step;
    }

    var _lastLogKey = '';
    function pushAgentLog(level, message, detail) {
      if (!message || level === 'status') return;
      var key = level + '|' + message;
      if (key === _lastLogKey && level !== 'error' && level !== 'warn') return;
      _lastLogKey = key;
      var entry = {
        ts: new Date().toLocaleTimeString(),
        level: level || 'info',
        message: message,
        detail: detail
      };
      vm.agentActivityLog.push(entry);
      if (vm.agentActivityLog.length > 80) vm.agentActivityLog.shift();
    }

    vm.getActiveStep = function () {
      if (vm.activeStepIndex == null) return null;
      return vm.streamingSteps.find(function (s) { return s.index === vm.activeStepIndex; }) || null;
    };

    function formatLogDetail(detail) {
      if (!detail) return '';
      if (typeof detail === 'string') return detail;
      if (typeof detail === 'object' && detail !== null) {
        var lines = [];
        var diagnosticKeys = ['hasUnquotedKeyNewString', 'hasUnquotedKeyOldString', 'extractJsonBlocks', 'repairChanged', 'endsWithClosingBrace', 'totalChars', 'hasMarkdownComment'];
        for (var k = 0; k < diagnosticKeys.length; k++) {
          var dk = diagnosticKeys[k];
          if (detail.hasOwnProperty(dk) && detail[dk] !== null && detail[dk] !== undefined) {
            lines.push(dk + ': ' + JSON.stringify(detail[dk]));
          }
        }
        if (lines.length > 0) return lines.join('\n') + '\n' + JSON.stringify(detail, null, 0);
      }
      try { return JSON.stringify(detail, null, 0); } catch (e) { return String(detail); }
    }

    function refreshFilesEditedFromSteps() {
      vm.streamingFilesEdited = vm.streamingSteps.filter(function (s) {
        return (s.type === 'edit' || s.type === 'rename') && s.status === 'done' && s.path;
      }).map(function (s) {
        var info = { path: s.path, editAction: s.editAction, linesAdded: s.linesAdded, linesRemoved: s.linesRemoved };
        if (s.type === 'rename') info.editAction = 'renamed → ' + (s.toPath || '');
        return info;
      });
    }

    function upsertStreamingStep(parsed) {
      normalizeStep(parsed);
      var existing = vm.streamingSteps.find(function (s) { return s.index === parsed.index; });
      if (existing) angular.extend(existing, parsed);
      else vm.streamingSteps.push(parsed);
      vm.streamingSteps.sort(function (a, b) { return (a.index || 0) - (b.index || 0); });
      if (parsed.status === 'running') vm.activeStepIndex = parsed.index;
      else {
        var running = vm.streamingSteps.find(function (s) { return s.status === 'running'; });
        vm.activeStepIndex = running ? running.index : null;
      }
      refreshFilesEditedFromSteps();
    }

    vm.splitCardIntoSubtasks = function (card) {
      if (!card || !card.text) return;
      var lines = card.text.split(/\n+/).map(function (l) { return l.trim(); }).filter(Boolean);
      if (lines.length <= 1) {
        var parts = card.text.split(/[.;]\s+/).filter(function (p) { return p.length > 10; });
        if (parts.length <= 1) return $window.alert('Task is already small. Add line breaks or bullet points to split.');
        lines = parts;
      }
      if (!$window.confirm('Split into ' + lines.length + ' smaller Todo cards?')) return;
      var idx = vm.state.todo.findIndex(function (c) { return c.id === card.id; });
      if (idx === -1) {
        ['doing', 'done'].forEach(function (col) {
          var i = vm.state[col].findIndex(function (c) { return c.id === card.id; });
          if (i !== -1) { vm.state[col].splice(i, 1); idx = -2; }
        });
      } else {
        vm.state.todo.splice(idx, 1);
      }
      lines.forEach(function (line, i) {
        vm.state.todo.push({
          id: uid(),
          text: line.charAt(0).toUpperCase() + line.slice(1),
          filePath: card.filePath || vm.selectedProject,
          createdAt: new Date().toISOString(),
          priority: card.priority || 'medium',
          attached: i === 0 ? angular.copy(card.attached || []) : []
        });
      });
      saveCards();
    };

    // === Diff display helpers ===

    vm.buildDiffLines = function (oldLines, newLines) {
      if (!oldLines) oldLines = [];
      if (!newLines) newLines = [];
      var maxLen = Math.max(oldLines.length, newLines.length);
      var result = [];
      for (var i = 0; i < maxLen; i++) {
        var oldLine = i < oldLines.length ? oldLines[i] : null;
        var newLine = i < newLines.length ? newLines[i] : null;
        var bothExist = oldLine !== null && newLine !== null;
        var changed = bothExist ? oldLine !== newLine : true;
        result.push({ oldLine: oldLine, newLine: newLine, changed: changed, bothExist: bothExist });
      }
      return result;
    };

    // === Agent Execution (streaming) ===

    vm.executeAgent = function (card) {
      if (!card) return;
      if (vm.streamingActive) return;
      if (!card.text) return $window.alert('Card has no task text');
      var proj = card.filePath || vm.selectedProject;
      if (!proj) return $window.alert('No project assigned');

      // Reset
      vm.agentResult = null;
      vm.aiResponse = '';
      vm.streamingThinking = '';
      vm.streamingSummary = '';
      vm.streamingPhase = '';
      vm.streamingSteps = [];
      vm.streamingFilesEdited = [];
      vm.agentActivityLog = [];
      vm.activeStepIndex = null;
      vm.lastPhaseLogged = '';
      _lastLogKey = '';
      vm.streamingActive = true;
      pushAgentLog('info', 'Agent started', { project: proj, task: card.text });
      vm.activeCardText = card.text;

      var files = card.attached || [];
      var payload = {
        prompt: card.text,
        project: proj,
        files: files,
        maxIterations: 5,
        maxStepsPerBatch: 8
      };

      // Move to Doing
      moveCardToDoing(card.id);

      vm.abortController = new AbortController();

      fetch('/api/agent/execute-stream', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
        signal: vm.abortController.signal
      }).then(function (response) {
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
          reader.read().then(function (result) {
            if (result.done) {
              vm.streamingActive = false;
              $scope.$digest();
              return;
            }
            buffer += decoder.decode(result.value, { stream: true });
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
                try { parsed = JSON.parse(data); } catch (e) { }

                switch (eventName) {
                  case 'log':
                    if (parsed) pushAgentLog(parsed.level, parsed.message, parsed.detail);
                    break;
                  case 'phase':
                    if (parsed && parsed.message) {
                      vm.streamingPhase = parsed.message;
                      if (parsed.message !== vm.lastPhaseLogged) {
                        vm.lastPhaseLogged = parsed.message;
                        pushAgentLog('phase', parsed.message);
                      }
                    } else if (parsed && parsed.phase) {
                      vm.streamingPhase = parsed.phase;
                    }
                    break;
                  case 'status':
                    if (parsed && parsed.message) vm.streamingPhase = parsed.message;
                    break;
                  case 'thinking':
                    if (parsed && parsed.text) {
                      vm.streamingThinking = parsed.text;
                      pushAgentLog('think', 'Plan updated', { len: parsed.text.length });
                    }
                    break;
                  case 'summary':
                    if (parsed && parsed.text) {
                      vm.streamingSummary = parsed.text;
                      pushAgentLog('summary', parsed.text);
                    }
                    break;
                  case 'step':
                    if (parsed) {
                      upsertStreamingStep(parsed);
                      if (parsed.status === 'running') {
                        pushAgentLog('step', '▶ ' + parsed.type + ': ' + (parsed.description || parsed.path || parsed.command || ''));
                      } else if (parsed.status === 'error') {
                        pushAgentLog('error', '✕ ' + parsed.type + ': ' + (parsed.error || parsed.description || ''));
                      }
                    }
                    break;
                  case 'done':
                    vm.streamingActive = false;
                    var editsApplied = parsed && parsed.editsApplied;
                    var incomplete = parsed && parsed.incomplete;
                    if (parsed && parsed.warning) vm.aiResponse = parsed.warning;
                    pushAgentLog(editsApplied ? 'info' : 'warn', editsApplied ? 'Agent finished' : 'Agent finished without file edits',
                      { filesEdited: (parsed && parsed.filesEdited) ? parsed.filesEdited.length : 0, warning: parsed && parsed.warning });
                    var finalThinking = (parsed && parsed.thinking) || vm.streamingThinking;
                    var finalSummary = (parsed && parsed.summary) || vm.streamingSummary;
                    var finalSteps = (parsed && parsed.steps) ? parsed.steps.map(normalizeStep) : angular.copy(vm.streamingSteps);
                    vm.streamingFilesEdited = (parsed && parsed.filesEdited) || [];
                    vm.agentResult = {
                      summary: finalSummary,
                      thinking: finalThinking,
                      filesEdited: vm.streamingFilesEdited,
                      steps: finalSteps,
                      warning: parsed && parsed.warning,
                      incomplete: incomplete
                    };
                    vm.aiResponse = (parsed && parsed.warning) || finalSummary || 'Agent completed.';
                    var analysis = {
                      summary: finalSummary,
                      thinking: finalThinking,
                      steps: finalSteps,
                      filesEdited: vm.streamingFilesEdited,
                      warning: parsed && parsed.warning,
                      incomplete: incomplete
                    };
                    var doIdx = vm.state.doing.findIndex(function (c) { return c.id === card.id; });
                    if (doIdx !== -1) {
                      vm.state.doing[doIdx].agentAnalysis = analysis;
                      vm.state.doing[doIdx].agentLog = angular.copy(vm.agentActivityLog);
                    }
                    if (incomplete) {
                      pushAgentLog('warn', 'Card kept in Doing — no files were modified');
                    } else {
                      moveCardToDone(card.id);
                    }
                    // Auto-queue next
                    if (vm.autoQueue) {
                      $timeout(function () { vm.processQueue(); }, 500);
                    }
                    break;
                  case 'error':
                    vm.streamingActive = false;
                    pushAgentLog('error', parsed ? parsed.message : data);
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
      }).catch(function (err) {
        vm.streamingActive = false;
        vm.abortController = null;
        if (err.name === 'AbortError') {
          vm.agentResult = { warning: 'Agent stopped by user.' };
        } else {
          vm.agentResult = { error: 'Connection failed: ' + err.message };
        }
        $scope.$digest();
      });
    };

    function moveCardToDoing(cardId) {
      var idx = vm.state.todo.findIndex(function (c) { return c.id === cardId; });
      if (idx === -1) return;
      var card = vm.state.todo.splice(idx, 1)[0];
      vm.state.doing.push(card);
      saveCards();
    }

    function moveCardToDone(cardId) {
      var idx = vm.state.doing.findIndex(function (c) { return c.id === cardId; });
      if (idx === -1) return;
      var card = vm.state.doing.splice(idx, 1)[0];
      vm.state.done.push(card);
      saveCards();
    }

    vm.startCard = function (card) {
      if (!card) return;
      if (card.ready) {
        moveCardToDoing(card.id);
        vm.executeAgent(card);
      } else {
        card.ready = true;
        saveCards();
      }
    };

    // === Auto-queue ===
    vm.processQueue = function () {
      if (vm.streamingActive) return;
      var next = vm.state.todo.filter(function (c) { return c.filePath === vm.selectedProject && c.ready; })[0];
      if (next) {
        moveCardToDoing(next.id);
        vm.executeAgent(next);
      }
    };

    // === AI Chat ===
    vm.askAI = function () {
      if (!vm.aiPrompt) return $window.alert('Enter a prompt');
      vm.agentResult = null;
      vm.aiResponse = 'Thinking...';
      $http.post('/api/ai/generate', { prompt: vm.aiPrompt })
        .then(function (resp) {
          if (typeof resp.data === 'string') vm.aiResponse = resp.data;
          else vm.aiResponse = JSON.stringify(resp.data, null, 2);
        }, function (err) { vm.aiResponse = 'Error: ' + (err.statusText || err); });
    };

    // === Terminal ===
    vm.startTerminal = function () { $http.post('/api/terminal/start').catch(function () { }); };
    vm.sendCmd = function () {
      if (!vm.termInput) return;
      $http.post('/api/terminal/exec', { command: vm.termInput }).then(function () {
        vm.termInput = '';
        vm.refreshTerminal();
      });
    };
    vm.refreshTerminal = function () {
      $http.get('/api/terminal/output').then(function (resp) { vm.terminalOutput = resp.data.output || ''; });
    };

    vm.stopAgent = function (card) {
      if (vm.abortController) {
        vm.abortController.abort();
        vm.abortController = null;
        vm.streamingActive = false;
        pushAgentLog('warn', 'Agent stopped by user');
      }
    };

    vm.formatLogDetail = formatLogDetail;
    vm.refreshTerminal();

    // === Column Resizers ===
    vm.initColumnResizers = function () {
      try {
        var existing = document.querySelectorAll('.col-resizer');
        existing.forEach(function (el) { el.remove(); });
        var cols = Array.prototype.slice.call(document.querySelectorAll('#board .column'));
        for (var i = 0; i < cols.length - 1; i++) {
          (function (leftCol) {
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
              var leftW = leftRect.width;
              var rightW = rightRect.width;
              var min = 200;
              document.body.style.userSelect = 'none';
              resizer.classList.add('active');
              function onMove(ev) {
                var dx = ev.clientX - startX;
                var nl = leftW + dx;
                var nr = rightW - dx;
                var total = leftW + rightW;
                if (nl < min) { nl = min; nr = total - min; }
                if (nr < min) { nr = min; nl = total - min; }
                leftCol.style.flex = '0 0 ' + Math.round(nl) + 'px';
                rightCol.style.flex = '0 0 ' + Math.round(nr) + 'px';
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
            resizer.addEventListener('dblclick', function () {
              cols.forEach(function (c) { c.style.flex = ''; c.style.width = ''; });
            });
          })(cols[i]);
        }
      } catch (e) { console.error('resizer error', e); }
    };

    $timeout(function () { vm.initColumnResizers(); }, 300);

    // === Drag & Drop between columns ===
    vm.setupDragDrop = function () {
      try {
        var cards = document.querySelectorAll('.card[draggable]');
        cards.forEach(function (c) {
          c.addEventListener('dragstart', function (e) {
            e.dataTransfer.setData('text/plain', c.id.replace('card-', ''));
            c.classList.add('dragging');
          });
          c.addEventListener('dragend', function (e) {
            c.classList.remove('dragging');
          });
        });
        var cols = document.querySelectorAll('.cards');
        cols.forEach(function (col) {
          col.addEventListener('dragover', function (e) { e.preventDefault(); col.closest('.column').classList.add('drop-target'); });
          col.addEventListener('dragleave', function (e) { col.closest('.column').classList.remove('drop-target'); });
          col.addEventListener('drop', function (e) {
            e.preventDefault();
            col.closest('.column').classList.remove('drop-target');
            var cardId = e.dataTransfer.getData('text/plain');
            var targetCol = col.closest('.column') ? col.closest('.column').getAttribute('data-col') : null;
            if (!cardId || !targetCol) return;
            // Find which column the card is currently in
            var fromCol = null;
            ['todo', 'doing', 'done'].forEach(function (cn) {
              var idx = vm.state[cn].findIndex(function (c) { return c.id === cardId; });
              if (idx !== -1) fromCol = cn;
            });
            if (!fromCol || fromCol === targetCol) return;
            var cardObj = vm.state[fromCol].find(function (c) { return c.id === cardId; });
            if (!cardObj) return;
            if (fromCol === 'todo' && targetCol === 'doing' && !cardObj.ready) {
              alert('Mark the card as Ready first (press Start)');
              return;
            }
            if (fromCol === 'doing' && targetCol === 'todo') {
              cardObj.ready = false;
              delete cardObj.agentAnalysis;
            }
            var idx = vm.state[fromCol].findIndex(function (c) { return c.id === cardId; });
            if (idx === -1) return;
            vm.state[fromCol].splice(idx, 1);
            vm.state[targetCol].push(cardObj);
            if (fromCol === 'todo' && targetCol === 'doing' && cardObj.ready) {
              saveCards();
              vm.executeAgent(cardObj);
              return;
            }
            saveCards();
            $scope.$digest();
          });
        });
      } catch (e) { console.error('dragdrop error', e); }
    };
    $timeout(function () { vm.setupDragDrop(); }, 500);

    // Refresh terminal periodically
    $interval(vm.refreshTerminal, 3000);
  }]);
