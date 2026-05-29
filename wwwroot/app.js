angular.module('kanbanApp', [])
  .controller('MainCtrl', ['$http', '$interval', '$window', '$scope', '$timeout', 'KanbanMixin', function ($http, $interval, $window, $scope, $timeout, KanbanMixin) {
    const vm = this;
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
    vm.activeCardIds = new Set();
    vm.autoQueue = true;
    vm.showTerminal = true;
    vm.showAI = true;
    vm.llamaUrl = "";
    vm.buildCommands = "";
    vm.terminalApprovalMode = 'approveAll';
    vm.approvedTerminalRoots = [];
    vm.approvedTerminalRootsText = '';
    vm.pendingTerminalApprovals = [];
    vm.aiChatMessages = [];
    vm.aiChatInput = '';
    vm.aiChatLoading = false;
    vm.searchFilter = '';
    vm.chatMode = 'ask';
    vm.faqs = [
      {
        question: 'How do I get started?',
        answer: 'To get started, simply create a new project and begin adding tasks to your Kanban board.',
        expanded: false
      },
      {
        question: 'Can I collaborate with others?',
        answer: 'Yes, you can invite team members to collaborate on projects and share Kanban boards.',
        expanded: false
      },
      {
        question: 'How do I export my data?',
        answer: 'You can export your Kanban data as JSON by clicking the export button in the settings panel.',
        expanded: false
      }
    ];

    // Build mode tool definitions
    vm.buildTools = [
      { name: 'Ping', icon: '📡', desc: 'Check host connectivity (TCP/ping/HTTP)', hint: 'ping google.com -n 4' },
      { name: 'Install Package', icon: '📦', desc: 'Install a NuGet/npm/pip package', hint: 'install package SonarAnalyzer.CSharp' },
      { name: 'Build', icon: '🔨', desc: 'Run build verification', hint: 'build the project' },
      { name: 'Full Agent', icon: '🤖', desc: 'Run the full agent pipeline', hint: 'refactor the login page' }
    ];
    vm.useToolHint = function (hint) {
      vm.aiChatInput = hint;
      var el = document.querySelector('.ai-chat-body input'); if (el) el.focus();
    };

    vm.toggleChatMode = function () { vm.chatMode = vm.chatMode === 'ask' ? 'build' : 'ask'; };

    // Agent streaming state
    vm.streamingActive = false;
    vm.streamingThinking = '';
    vm.streamingSummary = '';
    vm.streamingPhase = '';
    vm.streamingSteps = [];
    vm.streamingFilesEdited = [];
    vm.agentActivityLog = [];
    vm.agentActivityLogLength = 0;
    vm.activeStepIndex = null;
    vm.lastPhaseLogged = '';
    vm.agentResult = null;
    vm.clarificationReply = '';
    vm.abortController = null;
    vm.lastStreamingSteps = [];
    vm.lastStreamingPhase = '';
    vm.lastStreamingSummary = '';
    vm.lastStreamingThinking = '';
    vm.streamingStepsCopy = [];
    vm.lastStreamingStepsUpdate = 0;
    vm.planItems = [];

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

    // Export kanban data as JSON
    vm.exportKanbanData = function () {
      const data = JSON.stringify(vm.state);
      alert(data);
      return data;
    };

    // Scroll to bottom of agent log.
    // $timeout with invokeApply=false (third arg) so the DOM write never triggers
    // a digest cycle.  Target '.log-entries' — that is the actual scrollable div
    // inside the agent-activity-log <details> block.  '.agent-log' does not exist
    // in the template and querySelector returned null, making scrollToBottom a
    // silent no-op while still scheduling $timeout ticks that piled up.
    vm.scrollToBottom = function () {
      $timeout(function () {
        var logContainer = document.querySelector('.log-entries');
        if (logContainer) {
          logContainer.scrollTop = logContainer.scrollHeight;
        }
      }, 10, false);
    };

    // Auto-scroll agent log when new content is added.
    // NOTE: scrollToBottom is called by pushAgentLog, which is the real entry point
    // for all log pushes during streaming.  addLogEntry is kept for any direct
    // external callers but must NOT call scrollToBottom itself, otherwise every
    // pushAgentLog→addLogEntry chain would trigger two $timeout scroll calls.
    vm.addLogEntry = function (entry) {
      // Prevent duplicate entries that could cause digest loops
      if (vm.agentActivityLogLength > 0) {
        var lastEntry = vm.agentActivityLog[vm.agentActivityLogLength - 1];
        if (lastEntry.type === entry.type && lastEntry.message === entry.message) {
          return;
        }
      }
      // Prevent entries with same timestamp that could cause digest loops
      if (vm.agentActivityLogLength > 0) {
        var lastEntry = vm.agentActivityLog[vm.agentActivityLogLength - 1];
        if (lastEntry.timestamp === entry.timestamp) {
          return;
        }
      }
      vm.agentActivityLog.push(entry);
      vm.agentActivityLogLength = vm.agentActivityLog.length;
      // scrollToBottom intentionally omitted here — pushAgentLog handles it
    };

    // Project UI
    vm.showProjectOptions = false;
    vm.showEditProjectsPanel = false;
    vm.showSettingsPanel = false;
    vm.showDiscordPanel = false;
    vm.showFilePicker = false;
    vm.newProjectName = '';
    vm.newProjectPath = '';
    vm.newProjectDescription = '';
    vm.llamaUrl = 'http://localhost:8080';
    vm.showKanban = true;

    // File picker
    vm.pickerCardId = null;
    vm.pickerPath = '';
    vm.pickerEntries = [];
    vm.pickerSelected = [];

    // Settings
    vm.settingsDefaultProject = '';
    vm.fileHintsData = [];
    vm.emailImapServer = '';
    vm.emailImapPort = 993;
    vm.emailUseSsl = true;
    vm.emailUsername = '';
    vm.emailPassword = '';

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

    vm.saveFileHints = function () {
      var projectKey = vm.selectedProject || vm.defaultProject || '__default__';
      var hints = vm.fileHintsData.map(function (h) {
        return {
          Keywords: h.keywords.split(',').map(function (k) { return k.trim(); }).filter(Boolean),
          Files: h.files.filter(Boolean)
        };
      });
      var payload = { Projects: {} };
      payload.Projects[projectKey] = { Hints: hints, AutoLearned: [] };
      return $http.put('/api/filehints', payload).then(function () {
        vm.closeSettingsPanel();
      }, function (err) {
        $window.alert('Failed to save file hints: ' + (err.data || err.statusText || err));
      });
    };

    vm.addHint = function () {
      vm.fileHintsData.push({ keywords: '', files: [''] });
    };
    vm.removeHint = function (index) {
      vm.fileHintsData.splice(index, 1);
    };
    vm.addFileToHint = function (hintIndex) {
      vm.fileHintsData[hintIndex].files.push('');
    };
    vm.removeFileFromHint = function (hintIndex, fileIndex) {
      vm.fileHintsData[hintIndex].files.splice(fileIndex, 1);
    };

    vm.saveSettings = function () {
      saveSettings();
      $http.get('/api/config').then(function (resp) {
        var cfg = resp.data || { projects: vm.projects };
        cfg.projects = cfg.projects || vm.projects;
        cfg.defaultProject = vm.settingsDefaultProject || cfg.defaultProject || vm.defaultProject;
        cfg.showTerminal = vm.showTerminal !== false;
        cfg.showAI = vm.showAI !== false;
        cfg.showKanban = vm.showKanban !== false;
        cfg.llamaUrl = vm.llamaUrl || "http://localhost:8080";
        cfg.buildCommands = vm.buildCommands;
        cfg.terminalApprovalMode = vm.terminalApprovalMode || 'approveAll';
        cfg.approvedTerminalRoots = (vm.approvedTerminalRootsText || '').split(',').map(function (r) {
          return r.trim().toLowerCase();
        }).filter(Boolean);
        cfg.fileHints = '';
        cfg.emailImapServer = vm.emailImapServer || '';
        cfg.emailImapPort = vm.emailImapPort || 993;
        cfg.emailUseSsl = vm.emailUseSsl !== false;
        cfg.emailUsername = vm.emailUsername || '';
        cfg.emailPassword = vm.emailPassword || '';
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
        if (typeof cfg.showAI === 'boolean') vm.showAI = cfg.showAI;
        if (typeof cfg.showKanban === 'boolean') vm.showKanban = cfg.showKanban;
        vm.llamaUrl = cfg.llamaUrl || "http://localhost:8080";
        vm.buildCommands = cfg.buildCommands || "";
        vm.terminalApprovalMode = cfg.terminalApprovalMode || 'approveAll';
        vm.approvedTerminalRoots = cfg.approvedTerminalRoots || [];
        vm.approvedTerminalRootsText = vm.approvedTerminalRoots.join(', ');
        vm.fileHintsData = [];
        vm.emailImapServer = cfg.emailImapServer || '';
        vm.emailImapPort = cfg.emailImapPort || 993;
        vm.emailUseSsl = cfg.emailUseSsl !== false;
        vm.emailUsername = cfg.emailUsername || '';
        vm.emailPassword = cfg.emailPassword || '';
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

    vm.openEditProjectsPanel = function () {
      vm.newProjectName = '';
      vm.newProjectPath = '';
      vm.newProjectDescription = '';
      vm.projects.forEach(function (p) { p._origPath = p.Path; });
      vm.showEditProjectsPanel = true;
    };

    vm.closeEditProjectsPanel = function () {
      vm.showEditProjectsPanel = false;
    };

    vm.addProjectFromPanel = function () {
      if (!vm.newProjectName) return $window.alert('Project name is required');
      if (!vm.newProjectPath) return $window.alert('Project path is required');
      $http.post('/api/config/projects/add', {
        Name: vm.newProjectName,
        Path: vm.newProjectPath.replace(/\\/g, '/'),
        Description: vm.newProjectDescription || ''
      }).then(function () {
        vm.loadConfig();
        vm.newProjectName = '';
        vm.newProjectPath = '';
        vm.newProjectDescription = '';
      }, function (err) {
        $window.alert('Failed to add project: ' + (err.data || err.statusText));
      });
    };

    vm.saveProject = function (p) {
      if (!p.Name || !p.Path) return $window.alert('Name and Path are required');
      var originalPath = p._origPath || p.Path;
      $http.get('/api/config').then(function (resp) {
        var cfg = resp.data || { projects: [] };
        cfg.projects = cfg.projects || [];
        var idx = cfg.projects.findIndex(function (cp) { return (cp.Path || cp.path) === originalPath; });
        if (idx === -1) return $window.alert('Project not found in config');
        var newPath = p.Path.replace(/\\/g, '/');
        if (newPath !== originalPath && cfg.projects.some(function (cp) { return (cp.Path || cp.path) === newPath; }))
          return $window.alert('A project with that path already exists');
        cfg.projects[idx].Name = p.Name;
        cfg.projects[idx].Path = newPath;
        cfg.projects[idx].Description = p.Description || '';
        $http.post('/api/config/save', cfg).then(function () {
          vm.loadConfig();
        }, function (err) { $window.alert('Failed to save: ' + (err.data || err.statusText)); });
      });
    };

    vm.removeProject = function (p, event) {
      if (event) event.stopPropagation();
      if (!p || !p.Path) return;
      if (!$window.confirm('Remove project "' + (p.Name || '') + '" (' + p.Path + ')?')) return;
      $http.post('/api/config/projects/remove', { Path: p.Path }).then(function () { vm.loadConfig(); });
    };

    vm.openDiscordPanel = function () {
      vm.showDiscordPanel = true;
    };

    vm.closeDiscordPanel = function () {
      vm.showDiscordPanel = false;
    };

    vm.openSettingsPanel = function () {
      vm.settingsDefaultProject = vm.defaultProject || vm.selectedProject;
      vm.showSettingsPanel = true;
      vm.fileHintsData = [];
      $http.get('/api/filehints').then(function (resp) {
        try {
          var store = typeof resp.data === 'string' ? JSON.parse(resp.data) : resp.data;
          if (store && store.Projects) {
            var keys = Object.keys(store.Projects);
            var projKey = vm.selectedProject || keys[0];
            if (projKey && store.Projects[projKey]) {
              var proj = store.Projects[projKey];
              if (proj.Hints) {
                vm.fileHintsData = proj.Hints.map(function (h) {
                  return {
                    keywords: (h.Keywords || []).join(', '),
                    files: (h.Files || []).length > 0 ? h.Files.slice() : ['']
                  };
                });
              }
            } else if (keys.length) {
              var proj = store.Projects[keys[0]];
              if (proj.Hints) {
                vm.fileHintsData = proj.Hints.map(function (h) {
                  return {
                    keywords: (h.Keywords || []).join(', '),
                    files: (h.Files || []).length > 0 ? h.Files.slice() : ['']
                  };
                });
              }
            }
          }
        } catch (e) {
          vm.fileHintsData = [];
        }
      }, function () {
        vm.fileHintsData = [];
      });
      var backdrop = document.getElementById('backdrop');
      if (backdrop) {
        backdrop.style.display = 'block';
      }
    };
    vm.closeSettingsPanel = function (event) {
      if (event && event.target.tagName === 'INPUT') return;
      if (event) event.stopPropagation();
      vm.showSettingsPanel = false;
      // Hide backdrop when settings panel is closed
      var backdrop = document.getElementById('backdrop');
      if (backdrop) {
        backdrop.style.display = 'none';
      }
    };

    // === Cards (managed by KanbanMixin) ===
    KanbanMixin.init(vm, $scope);



    document.addEventListener('keydown', function (event) {
      if (event.key === 'Escape' && vm.deleteCardConfirm && vm.deleteCardConfirm.show) {
        console.log('ESC pressed - closing delete confirmation');
        vm.closeSettingsPanel();
        vm.closeEditProjectsPanel();
        vm.closeFilePicker();
        vm.closeDeleteCardConfirm();
      }
    });

    vm.selectCard = function (card) {
      vm.selectedCardId = card.id;
      vm.aiPrompt = card.text;
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
      vm.saveCards();
      vm.closeFilePicker();
    };

    // === Agent helpers ===

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
      vm.agentActivityLogLength = vm.agentActivityLog.length;
      if (vm.agentActivityLogLength > 80) vm.agentActivityLog.shift();
      // Scroll after every real push.  invokeApply=false (see scrollToBottom) keeps
      // this DOM write out of Angular's digest cycle so no infdig is possible.
      vm.scrollToBottom();
    }

    vm.getActiveStep = function () {
      if (vm.activeStepIndex == null) return null;
      return vm.streamingSteps.find(function (s) { return s.index === vm.activeStepIndex; }) || null;
    };

    function formatLogDetail(detail) {
      if (!detail) return '';
      if (typeof detail === 'string') return detail;
      if (typeof detail !== 'object') return String(detail);

      function fmt(val, indent) {
        if (val === null || val === undefined) return indent + '—';
        if (typeof val === 'boolean') return indent + (val ? 'yes' : 'no');
        if (typeof val === 'string') return indent + val;
        if (typeof val === 'number') return indent + String(val);

        if (Array.isArray(val)) {
          if (val.length === 0) return indent + '(empty)';
          var items = [];
          for (var i = 0; i < val.length; i++) {
            var item = val[i];
            var bullet = indent + '  ';
            if (item !== null && typeof item === 'object') {
              var inner = fmt(item, indent + '    ');
              var lines = inner.split('\n');
              lines[0] = bullet + '- ' + lines[0].trim();
              for (var j = 1; j < lines.length; j++) {
                lines[j] = bullet + '  ' + lines[j].trim();
              }
              items.push(lines.join('\n'));
            } else {
              items.push(bullet + '- ' + fmt(item, '').trim());
            }
          }
          return items.join('\n');
        }

        var keys = Object.keys(val);
        if (keys.length === 0) return indent + '(empty)';
        var lines = [];
        for (var i = 0; i < keys.length; i++) {
          var k = keys[i];
          var v = val[k];
          var label = k.replace(/([A-Z])/g, ' $1').replace(/^./, function (s) { return s.toUpperCase(); });
          if (v !== null && typeof v === 'object') {
            lines.push(indent + label + ':');
            lines.push(fmt(v, indent + '  '));
          } else {
            lines.push(indent + label + ': ' + fmt(v, '').trim());
          }
        }
        return lines.join('\n');
      }

      return fmt(detail, '');
    }

    function refreshFilesEditedFromSteps() {
      var seen = {};
      vm.streamingFilesEdited = vm.streamingSteps.filter(function (s) {
        return (s.type === 'edit' || s.type === 'rename') && s.status === 'done' && s.path;
      }).filter(function (s) {
        var already = seen[s.path];
        seen[s.path] = true;
        return !already;
      }).map(function (s) {
        var info = { path: s.path, editAction: s.editAction, linesAdded: s.linesAdded, linesRemoved: s.linesRemoved };
        if (s.type === 'rename') info.editAction = 'renamed → ' + (s.toPath || '');
        return info;
      });
    }

    function reconcilePlanItems() {
      if (!vm.planItems || !vm.planItems.length) return;
      vm.planItems.forEach(function (item) {
        if (item.done) return;
        var doneSteps = vm.streamingSteps.filter(function (s) {
          return s.status === 'done' && s.path && item.file &&
            s.path.replace(/\\/g, '/').toLowerCase() === item.file.toLowerCase();
        });
        if (doneSteps.length > 0) item.done = true;
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

    function findCardById(cardId) {
      if (!cardId || !vm.state) return null;
      var cols = ['todo', 'doing', 'done'];
      for (var c = 0; c < cols.length; c++) {
        var cards = vm.state[cols[c]] || [];
        for (var i = 0; i < cards.length; i++) {
          if (cards[i].id === cardId) return cards[i];
        }
      }
      return null;
    }

    vm.submitClarification = function () {
      var reply = (vm.clarificationReply || '').trim();
      if (!reply) return;
      var card = findCardById(vm.activeCardId);
      if (!card) {
        vm.aiChatMessages.push({ role: 'user', content: reply });
        vm.clarificationReply = '';
        return;
      }
      var question = (vm.agentResult && (vm.agentResult.question || vm.agentResult.summary)) || 'Clarification';
      card.text = (card.text || '') + '\n\nClarification requested: ' + question + '\nUser answer: ' + reply;
      delete card.agentAnalysis;
      delete card.agentLog;
      vm.saveCards();
      vm.clarificationReply = '';
      vm.agentResult = null;
      vm.executeAgent(card);
    };

    // === Agent Execution (streaming) ===

    vm.executeAgent = function (card) {
      if (!card) return;
      if (vm.streamingActive) return;
      if (!card.text) return $window.alert('Card has no task text');
      var proj = card.filePath || vm.selectedProject;
      if (!proj) return $window.alert('No project assigned');

      // Clear previous analysis for this fresh run
      delete card.agentAnalysis;
      delete card.agentLog;

      // Reset
      vm.agentResult = null;
      vm.aiResponse = '';
      vm.streamingThinking = '';
      vm.streamingSummary = '';
      vm.streamingPhase = '';
      vm.streamingSteps = [];
      vm.streamingFilesEdited = [];
      vm.planItems = [];
      vm.agentActivityLog = [];
      vm.activeStepIndex = null;
      vm.lastPhaseLogged = '';
      _lastLogKey = '';
      vm.streamingActive = true;
      pauseTerminalPolling();
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
      vm.moveCardToDoing(card.id);

      vm.activeCardId = card.id;
      if (!vm.activeCardIds) {
        vm.activeCardIds = new Set();
      }
      vm.activeCardIds.add(card.id);

      vm.abortController = new AbortController();

      fetch('/api/agent/execute-stream', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
        signal: vm.abortController.signal
      }).then(function (response) {
        if (!response.ok) {
          vm.streamingActive = false;
          resumeTerminalPolling();
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
              resumeTerminalPolling();
              try { $scope.$digest(); } catch (e) { /* infdig — already caught at line 857 */ }
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
                      pushAgentLog('think', 'Plan updated (Plan length: ' + parsed.text.length + ' chars)', { text: parsed.text });
                    }
                    break;
                  case 'summary':
                    if (parsed && parsed.text) {
                      vm.streamingSummary = parsed.text;
                      pushAgentLog('summary', parsed.text);
                    }
                    break;
                  case 'plan':
                    if (parsed && parsed.items && parsed.items.length) {
                      vm.planItems = parsed.items.map(function (item, i) {
                        return { index: i, file: item.File || item.file, change: item.Change || item.change, priority: item.Priority || item.priority, done: false };
                      });
                    }
                    break;
                  case 'show':
                    if (parsed && parsed.text) {
                      vm.aiResponse = parsed.text;
                      pushAgentLog('info', '📄 ' + parsed.text);
                    }
                    break;
                  case 'clarification':
                    if (parsed && parsed.question) {
                      vm.aiResponse = parsed.question;
                      pushAgentLog('warn', 'Clarification needed', { question: parsed.question });
                    }
                    break;
                  case 'step':
                    if (parsed) {
                      upsertStreamingStep(parsed);
                      reconcilePlanItems();
                      if (parsed.status === 'running') {
                        pushAgentLog('step', '▶ ' + parsed.type + ': ' + (parsed.description || parsed.path || parsed.command || ''));
                      } else if (parsed.status === 'error') {
                        pushAgentLog('error', '✕ ' + parsed.type + ': ' + (parsed.error || parsed.description || ''));
                      }
                    }
                    break;
                  case 'done':
                    vm.streamingActive = false;
                    resumeTerminalPolling();
                    var editsApplied = parsed && parsed.editsApplied;
                    var incomplete = parsed && parsed.incomplete;
                    if (parsed && parsed.warning) vm.aiResponse = parsed.warning;
                    pushAgentLog(editsApplied ? 'info' : 'warn', editsApplied ? 'Agent finished' : 'Agent finished without file edits',
                      { filesEdited: (parsed && parsed.filesEdited) ? parsed.filesEdited.length : 0, warning: parsed && parsed.warning });
                    var finalThinking = (parsed && parsed.thinking) || vm.streamingThinking;
                    var finalSummary = (parsed && parsed.summary) || vm.streamingSummary;
                    var finalSteps = (parsed && parsed.steps) ? parsed.steps.map(normalizeStep) : angular.copy(vm.streamingSteps);
                    if (parsed && parsed.filesEdited && parsed.filesEdited.length) {
                      vm.streamingFilesEdited = parsed.filesEdited;
                    } else {
                      refreshFilesEditedFromSteps();
                    }
                    vm.agentResult = {
                      summary: finalSummary,
                      thinking: finalThinking,
                      filesEdited: vm.streamingFilesEdited,
                      steps: finalSteps,
                      planItems: angular.copy(vm.planItems),
                      warning: parsed && parsed.warning,
                      incomplete: incomplete,
                      needsClarification: parsed && parsed.needsClarification,
                      question: parsed && (parsed.question || parsed.warning || finalSummary)
                    };
                    vm.aiResponse = (parsed && parsed.warning) || finalSummary || 'Agent completed.';
                    var analysis = {
                      summary: finalSummary,
                      thinking: finalThinking,
                      steps: finalSteps,
                      filesEdited: vm.streamingFilesEdited,
                      planItems: angular.copy(vm.planItems),
                      warning: parsed && parsed.warning,
                      incomplete: incomplete,
                      needsClarification: parsed && parsed.needsClarification,
                      question: parsed && (parsed.question || parsed.warning || finalSummary)
                    };
                    var doIdx = vm.state.doing.findIndex(function (c) { return c.id === card.id; });
                    if (doIdx !== -1) {
                      vm.state.doing[doIdx].agentAnalysis = analysis;
                      vm.state.doing[doIdx].agentLog = angular.copy(vm.agentActivityLog);
                    }
                    if (incomplete) {
                      pushAgentLog('warn', 'Card kept in Doing — no files were modified');
                    } else {
                      vm.moveCardToDone(card.id);
                    }
                    // Auto-queue next
                    if (vm.autoQueue) {
                      $timeout(function () { vm.processQueue(); }, 500);
                    }
                    break;
                  case 'error':
                    vm.streamingActive = false;
                    resumeTerminalPolling();
                    pushAgentLog('error', parsed ? parsed.message : data);
                    vm.agentResult = { error: parsed ? parsed.message : data };
                    vm.aiResponse = 'Error: ' + (parsed ? parsed.message : data);
                    break;
                }
              }
            }
            try { $scope.$digest(); } catch (e) { /* digest already in progress or infdig — skip */ }
            readNext();
          }).catch(function (readErr) {
            if (readErr && readErr.name === 'AbortError') return;
            vm.streamingActive = false;
            resumeTerminalPolling();
            vm.agentResult = { error: 'Stream read error: ' + (readErr && readErr.message || readErr) };
            try { $scope.$digest(); } catch (e) { }
          });
        }
        readNext();

      }).catch(function (err) {
        vm.streamingActive = false;
        resumeTerminalPolling();
        vm.abortController = null;
        if (err.name === 'AbortError') {
          vm.agentResult = { warning: 'Agent stopped by user.' };
        } else {
          vm.agentResult = { error: 'Connection failed: ' + err.message };
        }
        $scope.$digest();
      });
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

    vm.sendAiChat = function () {
      if (!vm.aiChatInput || vm.aiChatLoading) return;
      var userMsg = vm.aiChatInput;
      vm.aiChatInput = '';

      // Build mode — route to tools or agent pipeline
      if (vm.chatMode === 'build') {
        vm.aiChatMessages.push({ role: 'user', content: userMsg });
        vm.aiChatLoading = true;
        var lower = userMsg.toLowerCase();

        // Ping
        if (lower.includes('ping') || lower.includes('connect') || lower.includes('reachable')) {
          var hostMatch = userMsg.match(/(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}|[a-zA-Z0-9][a-zA-Z0-9.-]+\.[a-zA-Z]{2,})/);
          var portMatch = userMsg.match(/:(\d+)/);
          var payload = { host: hostMatch ? hostMatch[1] : 'localhost' };
          if (portMatch) payload.port = parseInt(portMatch[1]);
          $http.post('/api/terminal/ping', payload).then(function (resp) {
            var d = resp.data;
            var icon = d.success ? '✅' : '❌';
            vm.aiChatMessages.push({ role: 'assistant', content: icon + ' ' + d.method + ' — ' + d.host + (d.port ? ':' + d.port : '') + '\n\n```\n' + (d.output || '').substring(0, 2000) + '\n```' });
            vm.aiChatLoading = false;
          }, function (err) {
            vm.aiChatMessages.push({ role: 'assistant', content: '❌ Error: ' + (err.data?.error || err.statusText) }); vm.aiChatLoading = false;
          });
          return;
        }

        // Install package
        var pkgMatch = userMsg.match(/(?:install|add)\s+(?:package\s+)?(\S+)/i);
        if (pkgMatch) {
          $http.post('/api/terminal/install-package', { packageName: pkgMatch[1] }).then(function (resp) {
            var d = resp.data;
            var icon = d.success ? '✅' : '⚠️';
            vm.aiChatMessages.push({ role: 'assistant', content: icon + ' Package install ' + (d.success ? 'succeeded' : 'may have issues') + '\n\n```\n' + (d.output || '').substring(0, 2000) + '\n```' });
            vm.aiChatLoading = false;
          }, function (err) {
            vm.aiChatMessages.push({ role: 'assistant', content: '❌ Error: ' + (err.data?.error || err.statusText) }); vm.aiChatLoading = false;
          });
          return;
        }

        // Build / default — route to agent pipeline
        vm.aiChatLoading = false;
        // Create a temporary card and run the agent
        var tempCard = { id: 'chat-' + Date.now(), text: userMsg, project: vm.selectedProject, attached: [], ready: true };
        if (!tempCard.project) { vm.aiChatMessages.push({ role: 'assistant', content: '⚠️ No project selected. Select a project first.' }); return; }
        vm.streamingSummary = '';
        vm.streamingPhase = '';
        vm.streamingSteps = [];
        vm.agentActivityLog = [];
        vm.aiChatMessages.push({ role: 'assistant', content: '🤖 Starting agent pipeline...', _progress: true });
        vm.executeAgent(tempCard);
        // Poll for agent completion to show final result in chat
        var unwatch = $scope.$watch(function () { return vm.agentResult; }, function (newVal) {
          if (newVal) {
            var lastMsg = vm.aiChatMessages[vm.aiChatMessages.length - 1];
            if (lastMsg && lastMsg._progress) {
              lastMsg.content = newVal.error ? '❌ ' + newVal.error : (newVal.summary ? '✅ ' + newVal.summary : '✅ Agent completed');
              delete lastMsg._progress;
            }
            unwatch();
          }
        });
        return;
      }

      // Ask mode — simple chat
      vm.aiChatMessages.push({ role: 'user', content: userMsg });
      vm.aiChatLoading = true;
      var messages = vm.aiChatMessages.map(function (m) { return { role: m.role, content: m.content }; });
      $http.post('/api/ai/generate', { messages: messages })
        .then(function (resp) {
          var content = '';
          if (resp.data && resp.data.choices && resp.data.choices[0] && resp.data.choices[0].message) {
            content = resp.data.choices[0].message.content;
          } else if (typeof resp.data === 'string') {
            content = resp.data;
          } else if (resp.data && resp.data.content) {
            content = resp.data.content;
          } else {
            content = JSON.stringify(resp.data, null, 2);
          }
          vm.aiChatMessages.push({ role: 'assistant', content: content });
          vm.aiChatLoading = false;
        }, function (err) {
          vm.aiChatMessages.push({ role: 'assistant', content: 'Error: ' + (err.statusText || err) });
          vm.aiChatLoading = false;
        });
    };

    vm.clearAiChat = function () {
      vm.aiChatMessages = [];
      vm.aiChatInput = '';
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

    vm.refreshTerminalApprovals = function () {
      $http.get('/api/terminal/approvals/pending').then(function (resp) {
        vm.pendingTerminalApprovals = (resp.data && resp.data.approvals) || [];
      }, function () {
        vm.pendingTerminalApprovals = [];
      });
    };

    vm.approveTerminalCommand = function (approval, scope) {
      if (!approval) return;
      $http.post('/api/terminal/approvals/approve', { id: approval.id || approval.Id, scope: scope || 'once' })
        .then(function () {
          vm.refreshTerminalApprovals();
          vm.loadConfig();
        });
    };

    vm.rejectTerminalCommand = function (approval) {
      if (!approval) return;
      $http.post('/api/terminal/approvals/reject', { id: approval.id || approval.Id })
        .then(function () { vm.refreshTerminalApprovals(); });
    };

    vm.submitClarification = function () {
      var reply = (vm.clarificationReply || '').trim();
      if (!reply) return;
      var card = findCardById(vm.activeCardId);
      if (!card) {
        vm.aiChatMessages.push({ role: 'user', content: reply });
        vm.clarificationReply = '';
        return;
      }
      var question = (vm.agentResult && (vm.agentResult.question || vm.agentResult.summary)) || 'Clarification';
      card.text = (card.text || '') + '\n\nClarification requested: ' + question + '\nUser answer: ' + reply;
      delete card.agentAnalysis;
      delete card.agentLog;
      vm.saveCards();
      vm.clarificationReply = '';
      vm.agentResult = null;
      vm.executeAgent(card);
    };

    vm.submitQuestion = function () {
      if (!vm.pendingQuestion) return;
      var answers = {};
      var allFilled = true;
      vm.pendingQuestion.fields.forEach(function (f) {
        var val = (vm.questionAnswers[f.key] || '').trim();
        if (!val) allFilled = false;
        answers[f.key] = val;
      });
      if (!allFilled) {
        vm.questionError = 'Please fill in all fields (password is required).';
        return;
      }
      vm.questionError = '';
      $http.post('/api/agent/questions/answer', { id: vm.pendingQuestion.id, answers: answers }).then(function () {
        vm.showQuestionModal = false;
        vm.pendingQuestion = null;
      }, function (err) {
        vm.questionError = 'Failed to submit: ' + (err.data || err.statusText || err);
      });
    };

    vm.stopAgent = function (card) {
      if (vm.abortController) {
        vm.abortController.abort();
        vm.abortController = null;
      }
      vm.streamingActive = false;
      vm.agentResult = { warning: 'Agent stopped by user.' };
      pushAgentLog('warn', 'Agent stopped by user');
      if (card) {
        vm.activeCardIds.delete(card.id);
        vm.updateCardStatus(card.id, 'todo');
      }
      if (vm.activeCardIds.size === 0) {
        vm.streamingActive = false;
        vm.abortController = null;
        if (vm.state.todo.length > 0 && !vm.activeCardIds.size) {
          vm.moveCardToDoing(vm.state.todo[0].id);
        }
      }
    };

    vm.formatLogDetail = formatLogDetail;
    vm.refreshTerminal();
    vm.refreshTerminalApprovals();

    // Refresh terminal periodically — but NOT while the agent is streaming.
    // $interval always calls $apply after each tick.  When the agent is active,
    // $scope.$digest() is already being called manually in readNext() for every SSE
    // chunk.  Two concurrent digest sources produce the $rootScope:infdig loop
    // (Angular sees a watcher — the terminal output string length / scroll geometry —
    // still dirty after 10 passes and throws).  Pausing the interval during streaming
    // and resuming on done/error keeps exactly one digest source active at a time.
    var _terminalInterval = $interval(vm.refreshTerminal, 3000);
    var _approvalInterval = $interval(vm.refreshTerminalApprovals, 1500);

    function pauseTerminalPolling() {
      if (_terminalInterval) { $interval.cancel(_terminalInterval); _terminalInterval = null; }
      if (_approvalInterval) { $interval.cancel(_approvalInterval); _approvalInterval = null; }
    }
    function resumeTerminalPolling() {
      if (!_terminalInterval) _terminalInterval = $interval(vm.refreshTerminal, 3000);
      if (!_approvalInterval) _approvalInterval = $interval(vm.refreshTerminalApprovals, 1500);
    }
  }]);