angular.module('kanbanApp', [])
  .controller('MainCtrl', ['$http', '$interval', '$window', '$scope', '$timeout', 'KanbanMixin', 'CalendarMixin', function ($http, $interval, $window, $scope, $timeout, KanbanMixin, CalendarMixin) {
    const vm = this;
    const SETTINGS_KEY = 'maestroconfig.settings';

    // === State ===
    vm.selectedProject = '';
    vm.archiveCardCount = 0;
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
    vm.disallowedTerminalRoots = [];
    vm.approvedTerminalRootsText = '';
    vm.disallowedTerminalRootsText = '';
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
    vm.streamingContextSize = 0;
    vm.streamingSteps = [];
    vm.streamingFilesEdited = [];
    vm.agentActivityLog = [];
    vm.agentActivityLogLength = 0;
    vm.logFontSize = 10;
    vm.pendingContextReview = null;
    vm.contextReviewCountdown = 0;
    vm.contextReviewTimer = null;
    vm.activeStepIndex = null;
    vm.lastPhaseLogged = '';
    vm.agentResult = null;
    vm.steeringContext = '';
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

    vm.increaseLogFont = function () {
      vm.logFontSize = Math.min(vm.logFontSize + 2, 24);
    };
    vm.decreaseLogFont = function () {
      vm.logFontSize = Math.max(vm.logFontSize - 2, 6);
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
    vm.showCalendar = false;    
    vm.pickerCardId = null;
    vm.pickerPath = '';
    vm.pickerEntries = [];
    vm.pickerSelected = [];
    vm.isSearchResult = false;  
    vm.settingsDefaultProject = '';
    vm.fileHintsData = [];
    vm.emailAccounts = [];
    vm.bughostedUsername = '';
    vm.bughostedPassword = '';
    vm.bughostedHeartbeatEnabled = false;
    vm.prByDefault = false;
    vm.bughostedClientId = '';
    vm.bughostedStatus = 'disconnected';
    vm.bughostedTesting = false;
    vm.bughostedTestResult = '';
    vm.bughostedTestError = '';
    vm.remoteCommands = [];

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
      var payload = { Projects: {} };
      vm.fileHintsData.forEach(function (entry) {
        var projectKey = entry.projectPath || vm.selectedProject || vm.defaultProject || '__default__';
        payload.Projects[projectKey] = {
          Hints: entry.hints.map(function (h) {
            return {
              Keywords: h.keywords.split(',').map(function (k) { return k.trim(); }).filter(Boolean),
              Files: h.files.filter(Boolean)
            };
          }),
          AutoLearned: []
        };
      });
      return $http.put('/api/filehints', payload).then(function () {
        vm.closeSettingsPanel();
      }, function (err) {
        $window.alert('Failed to save file hints: ' + (err.data || err.statusText || err));
      });
    };

    vm.addHint = function (projectIndex) {
      if (vm.fileHintsData[projectIndex]) {
        vm.fileHintsData[projectIndex].hints.push({ keywords: '', files: [''] });
      }
    };
    vm.removeHint = function (projectIndex, hintIndex) {
      if (vm.fileHintsData[projectIndex]) {
        vm.fileHintsData[projectIndex].hints.splice(hintIndex, 1);
      }
    };
    vm.addFileToHint = function (projectIndex, hintIndex) {
      if (vm.fileHintsData[projectIndex] && vm.fileHintsData[projectIndex].hints[hintIndex]) {
        vm.fileHintsData[projectIndex].hints[hintIndex].files.push('');
      }
    };
    vm.removeFileFromHint = function (projectIndex, hintIndex, fileIndex) {
      if (vm.fileHintsData[projectIndex] && vm.fileHintsData[projectIndex].hints[hintIndex]) {
        vm.fileHintsData[projectIndex].hints[hintIndex].files.splice(fileIndex, 1);
      }
    };

    vm.saveSettings = function () {
      saveSettings();
      $http.get('/api/config').then(function (resp) {
        var cfg = resp.data || { projects: vm.projects };
        cfg.projects = cfg.projects || vm.projects;
        cfg.defaultProject = vm.settingsDefaultProject || vm.defaultProject;
        cfg.showTerminal = vm.showTerminal !== false;
        cfg.showAI = vm.showAI !== false;
        cfg.showKanban = vm.showKanban !== false;
        cfg.showCalendar = vm.showCalendar !== false;
        cfg.prByDefault = vm.prByDefault !== false;
        cfg.llamaUrl = vm.llamaUrl || "http://localhost:8080";
        cfg.buildCommands = vm.buildCommands;
        cfg.terminalApprovalMode = vm.terminalApprovalMode || 'approveAll';
        cfg.approvedTerminalRoots = (vm.approvedTerminalRootsText || '').split(',').map(function (r) {
          return r.trim().toLowerCase();
        }).filter(Boolean);
        cfg.disallowedTerminalRoots = (vm.disallowedTerminalRootsText || '').split(',').map(function (r) {
          return r.trim().toLowerCase();
        }).filter(Boolean);
        cfg.fileHints = '';
        cfg.emailAccounts = vm.emailAccounts.map(function(a) {
          return {
            imapServer: a.imapServer || '',
            imapPort: a.imapPort || 993,
            useSsl: a.useSsl !== false,
            username: a.username || '',
            password: a.password || '',
            label: a.label || ''
          };
        });
        cfg.bughostedUsername = vm.bughostedUsername || '';
        cfg.bughostedPassword = vm.bughostedPassword || '';
        cfg.bughostedHeartbeatEnabled = vm.bughostedHeartbeatEnabled || false;
        return $http.post('/api/config/save', cfg);
      }).then(function () {
        vm.defaultProject = vm.settingsDefaultProject || vm.defaultProject;
        if (vm.settingsDefaultProject) vm.selectedProject = vm.settingsDefaultProject;
        vm.loadConfig(vm.defaultProject);
        // Disconnect from bughosted if heartbeat was disabled
        if (!vm.bughostedHeartbeatEnabled && vm.bughostedClientId) {
          vm.bughostedLogout();
        }
        vm.closeSettingsPanel();
      }, function (err) {
        $window.alert('Failed to save settings: ' + (err.data || err.statusText || err));
      });
    };

    vm.addEmailAccount = function() {
      vm.emailAccounts.push({
        imapServer: '',
        imapPort: 993,
        useSsl: true,
        username: '',
        password: '',
        label: '',
        showAppPasswordInstructions: false,
        testing: false,
        testResult: null
      });
    };

    vm.removeEmailAccount = function(index) {
      vm.emailAccounts.splice(index, 1);
    };

    vm.checkEmailServer = function(index) {
      var acct = vm.emailAccounts[index];
      if (!acct) return;
      if (acct.imapServer) {
        var lower = acct.imapServer.toLowerCase();
        if (lower.includes('gmail.com') || lower.includes('googlemail.com')) {
          acct.showAppPasswordInstructions = 'google';
        } else if (lower.includes('outlook.com') || lower.includes('hotmail.com') || lower.includes('live.com') || lower.includes('msn.com')) {
          acct.showAppPasswordInstructions = 'microsoft';
        } else {
          acct.showAppPasswordInstructions = false;
        }
      } else {
        acct.showAppPasswordInstructions = false;
      }
    };

    vm.testEmailConnection = function(index) {
      var acct = vm.emailAccounts[index];
      if (!acct) return;
      if (!acct.imapServer || !acct.username || !acct.password) {
        acct.testResult = { success: false, message: 'Please fill in all fields' };
        return;
      }
      acct.testing = true;
      acct.testResult = null;
      $http.post('/api/email/test', {
        imapServer: acct.imapServer,
        imapPort: acct.imapPort,
        useSsl: acct.useSsl,
        username: acct.username,
        password: acct.password
      }).then(function(response) {
        acct.testing = false;
        acct.testResult = response.data;
      }).catch(function(error) {
        acct.testing = false;
        acct.testResult = { success: false, message: 'Connection test failed: ' + (error.data || error.statusText || 'Unknown error') };
      });
    };

    vm.countArchivedCards = function () {
      if (!vm.state || !vm.state.archived) {
        vm.archiveCardCount = 0;
        return;
      }

      // Handle different archived data structures
      if (Array.isArray(vm.state.archived)) {
        // If archived is an array, filter by current project
        vm.archiveCardCount = vm.state.archived.filter(function (card) {
          return card.filePath === vm.selectedProject;
        }).length;
      } else if (typeof vm.state.archived === 'object') {
        // If archived is an object, check if it has project keys
        var archivedData = vm.state.archived[vm.selectedProject];
        if (Array.isArray(archivedData)) {
          vm.archiveCardCount = archivedData.length;
        } else {
          vm.archiveCardCount = 0;
        }
      } else {
        vm.archiveCardCount = 0;
      }
    }; 
    
    // === Project config ===
    function normalizeProjects(raw) {
      return raw.map(function (p) { return { Name: p.Name || p.name, Path: p.Path || p.path, Description: p.Description || p.description || '' }; });
    }

    vm.loadConfig = function (project) {
      return $http.get('/api/config').then(function (resp) {
        var cfg = resp.data || {};
        var raw = (cfg.projects && cfg.projects.length) ? cfg.projects : [
          { Name: 'Project Alpha', Path: '../project-alpha' }
        ];
        vm.projects = normalizeProjects(raw);
        vm.selectedProject = project || cfg.defaultProject || (vm.projects.length ? vm.projects[0].Path : '');
        vm.defaultProject = project || cfg.defaultProject;
        if (typeof cfg.showTerminal === 'boolean') vm.showTerminal = cfg.showTerminal;
        if (typeof cfg.showAI === 'boolean') vm.showAI = cfg.showAI;
        if (typeof cfg.showKanban === 'boolean') vm.showKanban = cfg.showKanban;
        if (typeof cfg.showCalendar === 'boolean') vm.showCalendar = cfg.showCalendar;
        if (typeof cfg.prByDefault === 'boolean') vm.prByDefault = cfg.prByDefault;
        vm.llamaUrl = cfg.llamaUrl || "http://localhost:8080";
        vm.buildCommands = cfg.buildCommands || "";
        vm.terminalApprovalMode = cfg.terminalApprovalMode || 'approveAll';
        vm.approvedTerminalRoots = cfg.approvedTerminalRoots || [];
        vm.approvedTerminalRootsText = vm.approvedTerminalRoots.join(', ');
        vm.disallowedTerminalRoots = cfg.disallowedTerminalRoots || [];
        vm.disallowedTerminalRootsText = vm.disallowedTerminalRoots.join(', ');
        vm.fileHintsData = [];
        vm.emailAccounts = (cfg.emailAccounts || []).map(function(a) {
          return {
            imapServer: a.imapServer || '',
            imapPort: a.imapPort || 993,
            useSsl: a.useSsl !== false,
            username: a.username || '',
            password: a.password || '',
            label: a.label || '',
            showAppPasswordInstructions: false,
            testing: false,
            testResult: null
          };
        });
        // If no accounts but legacy fields exist, migrate
        if (vm.emailAccounts.length === 0 && (cfg.emailUsername || cfg.emailImapServer)) {
          var label = '';
          if (cfg.emailUsername && cfg.emailUsername.indexOf('@') > 0) {
            label = cfg.emailUsername.split('@')[0];
          }
          vm.emailAccounts.push({
            imapServer: cfg.emailImapServer || '',
            imapPort: cfg.emailImapPort || 993,
            useSsl: cfg.emailUseSsl !== false,
            username: cfg.emailUsername || '',
            password: cfg.emailPassword || '',
            label: label,
            showAppPasswordInstructions: false,
            testing: false,
            testResult: null
          });
        }
        vm.bughostedUsername = cfg.bughostedUsername || '';
        vm.bughostedPassword = cfg.bughostedPassword || '';
        vm.bughostedHeartbeatEnabled = cfg.bughostedHeartbeatEnabled || false;
        if (vm.bughostedHeartbeatEnabled && vm.bughostedUsername && !vm.bughostedClientId) {
          vm.bughostedLogin();
        }

      }, function () {
        vm.projects = normalizeProjects([{ Name: 'Default', Path: '..' }]);
        vm.selectedProject = '..';
        vm.defaultProject = '..';
      });

      console.log('Config loaded. Selected project:', vm.selectedProject, project);
      vm.countArchivedCards();
    };


    // Ensure at least one email account slot exists in the UI
    if (vm.emailAccounts.length === 0) {
      vm.addEmailAccount();
    }
    vm.loadConfig();
    startCalendarProcessing();

    vm.getSelectedProjectDescription = function () {
      if (!vm.selectedProject) return '';
      var p = vm.projects.find(function (p) { return (p.Path || p.path) === vm.selectedProject; });
      return p ? (p.Description || '') : '';
    };

    vm.toggleProjectOptions = function () { vm.showProjectOptions = !vm.showProjectOptions; }; 

vm.changeProject = function () { 
  console.log(vm.selectedProject); 
  vm.loadConfig(vm.selectedProject).then(function() {
    // Ensure cards are loaded before counting archived cards
    $timeout(function() {
      vm.countArchivedCards();
    }, 100);
  });  
};

    vm.openEditProjectsPanel = function () {
      vm.newProjectName = '';
      vm.newProjectPath = '';
      vm.newProjectDescription = '';
      vm.settingsDefaultProject = vm.defaultProject || vm.selectedProject;
      vm.projects.forEach(function (p) { p._origPath = p.Path; });
      vm.showEditProjectsPanel = true;
    };

    vm.closeEditProjectsPanel = function () {
      $http.get('/api/config').then(function (resp) {
        var cfg = resp.data || { projects: vm.projects };
        cfg.projects = cfg.projects || vm.projects;
        cfg.defaultProject = vm.settingsDefaultProject || cfg.defaultProject || vm.defaultProject;
        cfg.showTerminal = vm.showTerminal !== false;
        cfg.showAI = vm.showAI !== false;
        cfg.showKanban = vm.showKanban !== false;
        cfg.showCalendar = vm.showCalendar !== false;
        cfg.prByDefault = vm.prByDefault !== false;
        cfg.llamaUrl = vm.llamaUrl || "http://localhost:8080";
        cfg.buildCommands = vm.buildCommands;
        cfg.terminalApprovalMode = vm.terminalApprovalMode || 'approveAll';
        cfg.approvedTerminalRoots = (vm.approvedTerminalRootsText || '').split(',').map(function (r) {
          return r.trim().toLowerCase();
        }).filter(Boolean);
        cfg.disallowedTerminalRoots = (vm.disallowedTerminalRootsText || '').split(',').map(function (r) {
          return r.trim().toLowerCase();
        }).filter(Boolean);
        cfg.fileHints = '';
        cfg.emailAccounts = vm.emailAccounts.map(function(a) {
          return {
            imapServer: a.imapServer || '',
            imapPort: a.imapPort || 993,
            useSsl: a.useSsl !== false,
            username: a.username || '',
            password: a.password || '',
            label: a.label || ''
          };
        });
        cfg.bughostedUsername = vm.bughostedUsername || '';
        cfg.bughostedPassword = vm.bughostedPassword || '';
        cfg.bughostedHeartbeatEnabled = vm.bughostedHeartbeatEnabled || false;
        return $http.post('/api/config/save', cfg);
      }).then(function () {
        vm.defaultProject = vm.settingsDefaultProject || vm.defaultProject;
        if (vm.settingsDefaultProject) vm.selectedProject = vm.settingsDefaultProject;
        vm.loadConfig();
        vm.showEditProjectsPanel = false;
      }, function (err) {
        $window.alert('Failed to save settings: ' + (err.data || err.statusText || err));
        vm.showEditProjectsPanel = false;
      });
    };

    // === BugHosted.com Integration ===
    vm.bughostedLogin = function () {
      if (!vm.bughostedUsername || !vm.bughostedPassword) return;
      vm.bughostedStatus = 'connecting';
      $http.post('/api/bughosted/login', {
        Username: vm.bughostedUsername,
        Password: vm.bughostedPassword
      }).then(function (resp) {
        vm.bughostedClientId = resp.data.clientId;
        vm.bughostedStatus = 'connected';
        startBughostedHeartbeat();
        startBughostedCommandPolling();
      }, function () {
        vm.bughostedStatus = 'error';
        vm.bughostedClientId = '';
      });
    };

    vm.bughostedLogout = function () {
      if (vm.bughostedClientId) {
        $http.post('/api/bughosted/logout', { clientId: vm.bughostedClientId });
      }
      vm.bughostedClientId = '';
      vm.bughostedStatus = 'disconnected';
      stopBughostedHeartbeat();
      stopBughostedCommandPolling();
    };

    vm.bughostedToggle = function () {
      if (vm.bughostedStatus === 'connected' || vm.bughostedClientId) {
        vm.bughostedLogout();
      } else {
        vm.bughostedLogin();
      }
    };

    vm.bughostedTestConnection = function () {
      if (!vm.bughostedUsername || !vm.bughostedPassword) return;
      vm.bughostedTesting = true;
      vm.bughostedTestResult = '';
      $http.post('/api/bughosted/test', {
        Username: vm.bughostedUsername,
        Password: vm.bughostedPassword
      }).then(function (resp) {
        var d = resp.data;
        if (d.success) {
          vm.bughostedTestResult = 'ok';
        } else {
          vm.bughostedTestResult = 'fail';
          vm.bughostedTestError = d.error || 'HTTP ' + d.statusCode;
        }
        vm.bughostedTesting = false;
      }, function () {
        vm.bughostedTestResult = 'fail';
        vm.bughostedTestError = 'Cannot reach server';
        vm.bughostedTesting = false;
      });
    };

    vm.bughostedForceReconnect = function () {
      vm.bughostedLogout();
      _bhHeartbeatFailCount = 0;
      $timeout(function () {
        vm.bughostedLogin();
      }, 300);
    };

    var _bhHeartbeatFailCount = 0;
    var _bhHeartbeatTimer = null;
    function startBughostedHeartbeat() {
      stopBughostedHeartbeat();
      _bhHeartbeatTimer = $interval(function () {
        if (!vm.bughostedClientId || vm.bughostedStatus !== 'connected') return;
        var data = {
          clientId: vm.bughostedClientId,
          kanbanData: JSON.stringify({
            projects: (vm.projects || []).map(function (p) {
              return { Name: p.Name, Path: p.Path, Description: p.Description };
            }),
            state: vm.state,
            agentActive: vm.streamingActive || false,
            agentPhase: vm.streamingPhase || '',
            agentThinking: vm.streamingThinking || '',
            agentSummary: vm.streamingSummary || '',
            activeCardId: vm.activeCardId || null,
            activeCardText: vm.activeCardText || '',
            calendarCards: vm.calCards || []
          }),
          settings: JSON.stringify({
            llamaUrl: vm.llamaUrl,
            buildCommands: vm.buildCommands,
            terminalApprovalMode: vm.terminalApprovalMode,
            approvedTerminalRoots: vm.approvedTerminalRoots,
            disallowedTerminalRoots: vm.disallowedTerminalRoots,
            defaultProject: vm.defaultProject || vm.selectedProject,
            showTerminal: vm.showTerminal,
            showAI: vm.showAI,
            showKanban: vm.showKanban,
            showCalendar: vm.showCalendar,
            bughostedHeartbeatEnabled: vm.bughostedHeartbeatEnabled,
            bughostedUsername: vm.bughostedUsername,
            bughostedPassword: vm.bughostedPassword
          })
        };
        $http.post('/api/bughosted/heartbeat', data).then(function () {
          _bhHeartbeatFailCount = 0;
          vm.bughostedStatus = 'connected';
        }, function () {
          _bhHeartbeatFailCount++;
          if (_bhHeartbeatFailCount >= 3) {
            vm.bughostedStatus = 'error';
          }
        });
      }, 30000, 0, false);
    }
    function stopBughostedHeartbeat() {
      if (_bhHeartbeatTimer) { $interval.cancel(_bhHeartbeatTimer); _bhHeartbeatTimer = null; }
    }

    var _bhCommandTimer = null;
    function startBughostedCommandPolling() {
      stopBughostedCommandPolling();
      _bhCommandTimer = $interval(function () {
        if (!vm.bughostedClientId || vm.bughostedStatus !== 'connected') return;
        $http.get('/api/bughosted/commands?clientId=' + encodeURIComponent(vm.bughostedClientId))
          .then(function (resp) {
            var cmds = resp.data || [];
            cmds.forEach(function (cmd) {
              // parse JSON string into object
              if (cmd.parameters && !cmd.params) {
                try { cmd.params = JSON.parse(cmd.parameters); } catch (e) { cmd.params = {}; }
              }
              var existing = vm.remoteCommands.find(function (c) { return c.id === cmd.id; });
              if (!existing && cmd.command) {
                vm.remoteCommands.push(cmd);
                vm.executeRemoteCommand(cmd);
              }
            });
          });
      }, 15000, 0, false);
    }
    function stopBughostedCommandPolling() {
      if (_bhCommandTimer) { $interval.cancel(_bhCommandTimer); _bhCommandTimer = null; }
    }

    // === Calendar Event Processing ===
    var _calTimer = null;

    function cronMatches(expr, date) {
      try {
        var parts = expr.trim().split(/\s+/);
        if (parts.length !== 5) return false;
        function matchField(field, val) {
          if (field === '*') return true;
          if (field.indexOf('*/') === 0) {
            var interval = parseInt(field.slice(2), 10);
            return interval > 0 && val % interval === 0;
          }
          var vals = field.split(',');
          for (var i = 0; i < vals.length; i++) {
            if (parseInt(vals[i], 10) === val) return true;
          }
          return false;
        }
        return matchField(parts[0], date.getMinutes()) &&
               matchField(parts[1], date.getHours()) &&
               matchField(parts[2], date.getDate()) &&
               matchField(parts[3], date.getMonth() + 1) &&
               matchField(parts[4], date.getDay());
      } catch (e) { return false; }
    }

    function processCalendarEvents() {
      $http.get('/api/calendar/load').then(function (resp) {
        try {
          var data = resp.data;
          if (typeof data === 'string') data = JSON.parse(data);
          if (!Array.isArray(data)) return;
        } catch (e) { return; }

        var now = new Date();
        var todayStr = now.getFullYear() + '-' +
          String(now.getMonth() + 1).padStart(2, '0') + '-' +
          String(now.getDate()).padStart(2, '0');
        var currentMinutes = now.getHours() * 60 + now.getMinutes();
        var changed = false;

        for (var ci = 0; ci < data.length; ci++) {
          var cal = data[ci];
          if (!cal.date || !cal.text) continue;

          var shouldFire = false;

          if (cal.cronExpression) {
            // cron recurring entry
            if (cronMatches(cal.cronExpression, now)) {
              var lastFired = cal.lastFired ? new Date(cal.lastFired).getTime() : 0;
              // prevent double-firing within the same minute
              if (now.getTime() - lastFired > 60000) {
                shouldFire = true;
              }
            }
          } else {
            // one-shot entry
            if (cal.processed) continue;
            var calMinute = 0;
            if (cal.time) {
              var tp = cal.time.split(':');
              calMinute = parseInt(tp[0], 10) * 60 + parseInt(tp[1], 10);
            }
            if (cal.date < todayStr || (cal.date === todayStr && calMinute <= currentMinutes)) {
              shouldFire = true;
            }
          }

          if (shouldFire) {
            var newCard = {
              id: uid(),
              text: cal.text,
              filePath: cal.project || vm.selectedProject,
              createdAt: now.toISOString(),
              priority: cal.priority || 'medium',
              ready: true,
              attached: []
            };
            vm.state.todo.push(newCard);
            vm.saveCards();
            changed = true;

            if (cal.cronExpression) {
              cal.lastFired = now.toISOString();
            } else {
              cal.processed = true;
            }

            if (!vm.streamingActive) {
              vm.executeAgent(newCard);
            }
          }
        }

        if (changed) {
          $http.post('/api/calendar/save', data).catch(function () {});
        }
      }, function () {});
    }

    function startCalendarProcessing() {
      stopCalendarProcessing();
      _calTimer = $interval(processCalendarEvents, 60000, 0, false);
      processCalendarEvents();
    }

    function stopCalendarProcessing() {
      if (_calTimer) { $interval.cancel(_calTimer); _calTimer = null; }
    }

    function findCardColumn(cardId) {
      if (!cardId || !vm.state) return null;
      var cols = ['todo', 'doing', 'done', 'archived'];
      for (var i = 0; i < cols.length; i++) {
        var cards = vm.state[cols[i]] || [];
        for (var j = 0; j < cards.length; j++) {
          if (cards[j].id === cardId) return cols[i];
        }
      }
      return null;
    }

    function uid() { return Math.random().toString(36).slice(2, 9); }
    
    vm.executeRemoteCommand = function (cmd) {
      console.log('Executing remote command:', cmd);
      if (cmd.command === 'executeTask' && cmd.params && cmd.params.text) {
        console.log('Executing task command from remote:', cmd);
        var card = {
          id: uid(),
          text: cmd.params.text,
          filePath: cmd.params.project || vm.selectedProject,
          createdAt: new Date().toISOString(),
          priority: cmd.params.priority || 'medium',
          attached: []
        };
        vm.state.todo.push(card);
        vm.saveCards();
      } else if (cmd.command === 'addCard') {
        console.log('Adding card from remote command:', cmd);
        var card = {
          id: cmd.params.cardId || uid(),
          text: cmd.params.text || cmd.params.title || '',
          filePath: cmd.params.project || vm.selectedProject,
          createdAt: new Date().toISOString(),
          priority: cmd.params.priority || 'medium',
          attached: []
        };
        vm.state.todo.push(card);
        console.log('Added card from remote command:', card);
        vm.saveCards();
      } else if (cmd.command === 'moveCard' && cmd.params) {
        console.log('Moving card from remote command:', cmd);
        var fromCol = findCardColumn(cmd.params.cardId);
        if (fromCol && cmd.params.status && fromCol !== cmd.params.status) {
          vm.moveCard(cmd.params.cardId, fromCol, cmd.params.status);
        }
      } else if (cmd.command === 'updateCard' && cmd.params) {
        console.log('Updating card from remote command:', cmd);
        var c = findCardById(cmd.params.cardId);
        if (c) {
          if (cmd.params.text) c.text = cmd.params.text;
          if (cmd.params.priority) c.priority = cmd.params.priority;
          if (cmd.params.attached !== undefined) c.attached = cmd.params.attached;
          if (cmd.params.autoPr !== undefined) c.autoPr = cmd.params.autoPr;
          vm.saveCards();
        }
      } else if (cmd.command === 'archiveCard' && cmd.params) {
        console.log('Archiving card from remote command:', cmd);
        var col = findCardColumn(cmd.params.cardId) || 'done';
        vm.archiveCard(cmd.params.cardId, col);
      } else if (cmd.command === 'startAgent' && cmd.params) {
        console.log('Starting agent from remote command:', cmd);
        var c = findCardById(cmd.params.cardId);
        if (c && !vm.streamingActive) {
          vm.executeAgent(c);
        }
      } else if (cmd.command === 'stopAgent') {
        console.log('Stopping agent from remote command:', cmd);
        var activeCard = findCardById(vm.activeCardId);
        vm.stopAgent && vm.stopAgent(activeCard);
      } else if (cmd.command === 'startPr' && cmd.params && cmd.params.cardId) {
        console.log('Starting PR from remote command:', cmd);
        var project = cmd.params.project || vm.selectedProject;
        if (project) {
          $http.post('/api/pr/start', {
            projectPath: project,
            cardId: cmd.params.cardId,
            cardText: cmd.params.text || ''
          }).then(function (resp) {
            var d = resp.data;
            var c = findCardById(cmd.params.cardId);
            if (c) {
              if (d && d.success) {
                c.prStatus = { status: 'branch-created', branch: d.branchName, originalBranch: d.originalBranch };
                c.autoPr = true;
              } else {
                c.prStatus = { status: 'error', error: (d && d.error) || 'Branch creation failed' };
              }
              vm.saveCards();
            }
          }, function (err) {
            var c = findCardById(cmd.params.cardId);
            if (c) { c.prStatus = { status: 'error', error: err.statusText || 'Network error' }; vm.saveCards(); }
          });
        }
      } else if (cmd.command === 'finishPr' && cmd.params && cmd.params.cardId) {
        console.log('Finishing PR from remote command:', cmd);
        var project = cmd.params.project || vm.selectedProject;
        if (project) {
          $http.post('/api/pr/finish', {
            projectPath: project,
            cardId: cmd.params.cardId,
            cardText: cmd.params.text || '',
            branchName: cmd.params.branchName || '',
            summary: cmd.params.summary || '',
            originalBranch: cmd.params.originalBranch || ''
          }).then(function (resp) {
            var d = resp.data;
            var c = findCardById(cmd.params.cardId);
            if (c) {
              if (d && d.success) {
                c.prStatus = { status: 'pr-created', branch: d.branchName || cmd.params.branchName, prUrl: d.prUrl || '' };
              } else {
                c.prStatus = { status: 'error', error: (d && d.error) || 'PR creation failed', branch: cmd.params.branchName };
              }
              vm.saveCards();
            }
          }, function (err) {
            var c = findCardById(cmd.params.cardId);
            if (c) { c.prStatus = { status: 'error', error: err.statusText || 'PR failed', branch: cmd.params.branchName }; vm.saveCards(); }
          });
        }
      } else if (cmd.command === 'deleteCard' && cmd.params && cmd.params.cardId) {
        console.log('Deleting card from remote command:', cmd);
        var col = findCardColumn(cmd.params.cardId);
        if (col) {
          var cards = vm.state[col] || [];
          var idx = cards.findIndex(function(c) { return c.id === cmd.params.cardId; });
          if (idx !== -1) cards.splice(idx, 1);
          vm.saveCards();
        }
      } else if (cmd.command === 'changeCardText' && cmd.params && cmd.params.cardId) {
        console.log('Changing card text from remote command:', cmd);
        var card = findCardById(cmd.params.cardId);
        if (card && cmd.params.text !== undefined) {
          card.text = cmd.params.text;
          vm.saveCards();
        }
      } else if (cmd.command === 'updateSettings' && cmd.params) {
        console.log('Updating settings from remote command:', cmd);
        if (cmd.params.llamaUrl !== undefined) vm.llamaUrl = cmd.params.llamaUrl;
        if (cmd.params.buildCommands !== undefined) vm.buildCommands = cmd.params.buildCommands;
        if (cmd.params.terminalApprovalMode !== undefined) vm.terminalApprovalMode = cmd.params.terminalApprovalMode;
        if (cmd.params.approvedTerminalRoots !== undefined) {
          vm.approvedTerminalRoots = cmd.params.approvedTerminalRoots;
          vm.approvedTerminalRootsText = (cmd.params.approvedTerminalRoots || []).join(', ');
        }
        if (cmd.params.disallowedTerminalRoots !== undefined) {
          vm.disallowedTerminalRoots = cmd.params.disallowedTerminalRoots;
          vm.disallowedTerminalRootsText = (cmd.params.disallowedTerminalRoots || []).join(', ');
        }
        if (cmd.params.defaultProject !== undefined) vm.settingsDefaultProject = cmd.params.defaultProject;
        if (cmd.params.showTerminal !== undefined) vm.showTerminal = cmd.params.showTerminal;
        if (cmd.params.showAI !== undefined) vm.showAI = cmd.params.showAI;
        if (cmd.params.showKanban !== undefined) vm.showKanban = cmd.params.showKanban;
        if (cmd.params.showCalendar !== undefined) vm.showCalendar = cmd.params.showCalendar;
        if (cmd.params.prByDefault !== undefined) vm.prByDefault = cmd.params.prByDefault;
        if (cmd.params.bughostedHeartbeatEnabled !== undefined) vm.bughostedHeartbeatEnabled = cmd.params.bughostedHeartbeatEnabled;
        if (cmd.params.bughostedUsername !== undefined) vm.bughostedUsername = cmd.params.bughostedUsername;
        if (cmd.params.bughostedPassword !== undefined) vm.bughostedPassword = cmd.params.bughostedPassword;
        vm.saveSettings();
      } else if (cmd.command === 'addCalendarCard' && cmd.params) {
        console.log('Adding calendar card from remote command:', cmd);
        if (!Array.isArray(vm.calCards)) vm.calCards = [];
        var calCard = {
          id: uid(),
          date: cmd.params.date || '',
          time: cmd.params.time || '',
          text: cmd.params.text || '',
          priority: cmd.params.priority || 'medium',
          cronExpression: cmd.params.cronExpression || '',
          project: cmd.params.project || vm.selectedProject,
          createdAt: new Date().toISOString()
        };
        vm.calCards.push(calCard);
        $http.post('/api/calendar/save', vm.calCards);
      } else if (cmd.command === 'updateCalendarCard' && cmd.params && cmd.params.id) {
        console.log('Updating calendar card from remote command:', cmd);
        if (!Array.isArray(vm.calCards)) return;
        var idx = -1;
        for (var ci = 0; ci < vm.calCards.length; ci++) {
          if (vm.calCards[ci].id === cmd.params.id) { idx = ci; break; }
        }
        if (idx !== -1) {
          if (cmd.params.date !== undefined) vm.calCards[idx].date = cmd.params.date;
          if (cmd.params.time !== undefined) vm.calCards[idx].time = cmd.params.time;
          if (cmd.params.text !== undefined) vm.calCards[idx].text = cmd.params.text;
          if (cmd.params.priority !== undefined) vm.calCards[idx].priority = cmd.params.priority;
          if (cmd.params.cronExpression !== undefined) vm.calCards[idx].cronExpression = cmd.params.cronExpression;
          if (cmd.params.project !== undefined) vm.calCards[idx].project = cmd.params.project;
          $http.post('/api/calendar/save', vm.calCards);
        }
      } else if (cmd.command === 'deleteCalendarCard' && cmd.params && cmd.params.id) {
        console.log('Deleting calendar card from remote command:', cmd);
        if (!Array.isArray(vm.calCards)) return;
        var filtered = [];
        for (var ci = 0; ci < vm.calCards.length; ci++) {
          if (vm.calCards[ci].id !== cmd.params.id) filtered.push(vm.calCards[ci]);
        }
        vm.calCards = filtered;
        $http.post('/api/calendar/save', vm.calCards);
      }
      // Acknowledge execution
      $http.post('/api/bughosted/commands/ack', {
        clientId: vm.bughostedClientId,
        commandId: cmd.id,
        status: 'executed',
        result: 'ok'
      });
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
            vm.projects.forEach(function (p) {
              var proj = store.Projects[p.Path];
              vm.fileHintsData.push({
                projectPath: p.Path,
                hints: proj && proj.Hints ? proj.Hints.map(function (h) {
                  return {
                    keywords: (h.Keywords || []).join(', '),
                    files: (h.Files || []).length > 0 ? h.Files.slice() : ['']
                  };
                }) : []
              });
            });
          } else {
            vm.projects.forEach(function (p) {
              vm.fileHintsData.push({ projectPath: p.Path, hints: [] });
            });
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
    vm.countArchivedCards();

    // === Calendar (managed by CalendarMixin) ===
    CalendarMixin.init(vm, $scope);



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
      vm.openFilePicker(cardId, '');
      
      $timeout(function () {
        vm.loadPickerEntries(cardId);  
      }, 30);
    };

    vm.closeFilePicker = function () {
      vm.showFilePicker = false;
      vm.pickerCardId = null;
      vm.pickerPath = '';
      vm.pickerEntries = [];
      vm.pickerSelected = [];
      vm.isSearchResult = false;
      vm.searchFilter = '';
    };

    vm.openFilePicker = function (cardId, path) {
      vm.showFilePicker = true;
      vm.pickerCardId = cardId;
      vm.pickerPath = path;
      vm.pickerEntries = [];
      vm.pickerSelected = [];
      vm.isSearchResult = false;
      vm.searchFilter = '';
      $timeout(function () {
        var searchInput = document.getElementById('attachFilePickerSearchInput');
        if (searchInput) {
          searchInput.focus();
        }
      }, 10);
    };

    vm.loadPickerEntries = function (cardId) {
      var params = { project: vm.selectedProject };
      if (vm.searchFilter && vm.searchFilter.trim()) {
        params.search = vm.searchFilter.trim();
        // Preserve the current path for search operations to ensure they stay within the current directory
        if (vm.pickerPath) {
          params.path = vm.pickerPath;
        }
      } else if (vm.pickerPath) {
        params.path = vm.pickerPath;
      }
      $http.get('/api/editor/list', { params: params }).then(function (resp) {
        vm.pickerEntries = (resp.data && resp.data.entries) || [];
        vm.isSearchResult = !!(resp.data && resp.data.search);
      }, function () { vm.pickerEntries = []; });
    };

    vm.pickerEnterDir = function (path) {
      vm.pickerPath = path;
      vm.searchFilter = '';
      vm.isSearchResult = false;
      vm.loadPickerEntries();
    };

    vm.pickerUpDir = function () {
      if (!vm.pickerPath) return;
      var segs = vm.pickerPath.split('/').filter(function (s) { return s && s.length; });
      segs.pop();
      vm.pickerPath = segs.join('/');
      vm.loadPickerEntries();
    };

    // Debounce function to prevent rapid successive searches
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

    // Debounced version of onSearchChange
    vm.debouncedOnSearchChange = debounce(function() {
      vm.loadPickerEntries();
    }, 300); // 300ms delay

    vm.onSearchChange = function () {
      vm.debouncedOnSearchChange();
    };

    vm.clearSearch = function () {
      vm.searchFilter = '';
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

      // Normalise by stripping all digits so "Plan score: 88/100" and
      // "Plan score: 90/100" are treated as duplicates and suppressed.
      function normalise(s) { return (s || '').replace(/\d+/g, '#'); }

      var recentDupe = vm.agentActivityLog.length > 0 &&
        vm.agentActivityLog.slice(-3).some(function (e) {
          return e.level === level && normalise(e.message) === normalise(message);
        });

      if (recentDupe && level !== 'error' && level !== 'warn') return;

      var entry = {
        ts: new Date().toLocaleTimeString(),
        level: level || 'info',
        message: message,
        detail: detail
      };
      vm.agentActivityLog.push(entry);
      vm.agentActivityLogLength = vm.agentActivityLog.length;
      if (vm.agentActivityLogLength > 100) vm.agentActivityLog.shift(); // was 80
      vm.scrollToBottom();
    }

    vm.getActiveStep = function () {
      if (vm.activeStepIndex == null) return null;
      return vm.streamingSteps.find(function (s) { return s.index === vm.activeStepIndex; }) || null;
    };


    function formatLogDetail(detail) {
      if (!detail) return '';

      // If detail is a plain string, try to detect + pretty-print embedded JSON.
      // The backend logs the raw LLM output (which failed to parse) as a string.
      // Displaying it as formatted JSON makes the bug immediately obvious.
      if (typeof detail === 'string') {
        var trimmed = detail.trim();
        if ((trimmed.startsWith('{') || trimmed.startsWith('[')) && trimmed.length > 2) {
          try {
            return JSON.stringify(JSON.parse(trimmed), null, 2);
          } catch (e) {
            // Not valid JSON — show with a clear warning so the user knows
            // this is the broken output that caused the parse failure.
            return '⚠ (NOT valid JSON — this is why parsing failed)\n\n' + detail;
          }
        }
        return detail;
      }

      if (typeof detail !== 'object') return String(detail);

      // Recursive pretty-formatter for objects/arrays (unchanged from original).
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
              var ls = inner.split('\n');
              ls[0] = bullet + '- ' + ls[0].trim();
              for (var j = 1; j < ls.length; j++)
                ls[j] = bullet + '  ' + ls[j].trim();
              items.push(ls.join('\n'));
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
          var label = k
            .replace(/([A-Z])/g, ' $1')
            .replace(/^./, function (s) { return s.toUpperCase(); });
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

    vm.executeAgent = function (card) {
      if (!card) return;
      if (vm.streamingActive) return;
      if (!card.text) return $window.alert('Card has no task text');
      var proj = card.filePath || vm.selectedProject;
      if (!proj) return $window.alert('No project assigned');

      // Clear previous analysis for this fresh run
      delete card.agentAnalysis;
      delete card.agentLog;
      // If task text changed, remove context-discovered files from attachments
      if (card.confirmedContextFiles && card._lastRunText && card.text !== card._lastRunText) {
        var remove = card.confirmedContextFiles;
        var attached = Array.isArray(card.attached) ? card.attached : (card.attached ? [card.attached] : []);
        card.attached = attached.filter(function (f) { return remove.indexOf(f) === -1; });
        delete card.confirmedContextFiles;
      }

      function startAgent() {
        // Reset
        vm.agentResult = null;
        vm.aiResponse = '';
        vm.streamingThinking = '';
        vm.streamingSummary = '';
        vm.streamingPhase = '';
        vm.streamingContextSize = 0;
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
        card._lastRunText = card.text;

        var files = Array.isArray(card.attached) ? card.attached : (card.attached ? [card.attached] : []);
        var payload = {
          prompt: card.text,
          project: proj,
          files: files,
          maxIterations: 5,
          maxStepsPerBatch: 8,
          steeringContext: vm.steeringContext || ''
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
            $scope.$applyAsync();
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

                var eventLineFound = false;
                for (var l = 0; l < lines.length; l++) {
                  var line = lines[l];
                  if (!eventLineFound && line.startsWith('event: ')) {
                    eventName = line.slice(7).trim();
                    eventLineFound = true;
                  } else if (line.startsWith('data: ')) {
                    if (data) data += '\n';
                    data += line.slice(6);
                  }
                }

                data = data.trimEnd ? data.trimEnd() : data.replace(/\s+$/, '');

                if (eventName) {
                  var parsed = null;
                  try { parsed = JSON.parse(data); } catch (e) {
                    if (data && data.length > 2) {
                      console.warn('[SSE] Failed to parse data for event "' + eventName + '":', data.slice(0, 120));
                    }
                  }

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
                      if (parsed && parsed.contextSize) {vm.streamingContextSize = parsed.contextSize;}
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
                          return { index: i, file: item.File || item.file || '?', change: item.Change || item.change || '', priority: item.Priority || item.priority || i + 1, done: false };
                        });
                        if (parsed.summary) {
                          pushAgentLog('info', '📋 Plan: ' + parsed.summary, { itemCount: parsed.items.length, score: parsed.score });
                        }
                      } else if (parsed && parsed.score !== undefined) {
                        pushAgentLog('warn', 'Plan returned score ' + parsed.score + '/100 but has no items — check logs', parsed);
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
                    case 'context-review':
                      try {
                        if (parsed && parsed.id && parsed.files) {
                          const ctx = parsed;
                          ctx.files.forEach(function (f) { f.keep = true; });
                          vm.pendingContextReview = ctx;
                          vm._contextReviewSubmitted = false;
                          vm.contextReviewCountdown = 5;
                          pushAgentLog('phase', '📋 Context review — ' + ctx.files.length + ' file(s) discovered, auto-confirm in 5s');
                          if (vm.contextReviewTimer) { $interval.cancel(vm.contextReviewTimer); }
                          if (vm.contextReviewAutoConfirm) { $timeout.cancel(vm.contextReviewAutoConfirm); }
                          vm.contextReviewTimer = $interval(function () {
                            vm.contextReviewCountdown--;
                            if (vm.contextReviewCountdown < 0) vm.contextReviewCountdown = 0;
                          }, 1000, 5, false);
                          vm.contextReviewAutoConfirm = $timeout(function () {
                            if (!vm._contextReviewSubmitted && vm.pendingContextReview) {
                              vm.confirmContextReview();
                            }
                          }, 5000);
                        }
                      } catch (e) {
                        pushAgentLog('error', 'Context review error: ' + (e.message || e));
                      }
                      break;
                    case 'done':
                      vm.streamingActive = false;
                      resumeTerminalPolling();
                      vm.steeringContext = '';
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
                        summary: finalSummary, thinking: finalThinking, filesEdited: vm.streamingFilesEdited,
                        steps: finalSteps, planItems: angular.copy(vm.planItems), warning: parsed && parsed.warning,
                        incomplete: incomplete, needsClarification: parsed && parsed.needsClarification,
                        question: parsed && (parsed.question || parsed.warning || finalSummary)
                      };
                      vm.aiResponse = (parsed && parsed.warning) || finalSummary || 'Agent completed.';
                      var analysis = {
                        summary: finalSummary, thinking: finalThinking, steps: finalSteps,
                        filesEdited: vm.streamingFilesEdited, planItems: angular.copy(vm.planItems),
                        warning: parsed && parsed.warning, incomplete: incomplete,
                        needsClarification: parsed && parsed.needsClarification,
                        question: parsed && (parsed.question || parsed.warning || finalSummary)
                      };
                      var doIdx = vm.state.doing.findIndex(function (c) { return c.id === card.id; });
                      if (doIdx !== -1) {
                        vm.state.doing[doIdx].agentAnalysis = analysis;
                        vm.state.doing[doIdx].agentLog = angular.copy(vm.agentActivityLog);
                      }

                      function finishCard() {
                        if (!incomplete) {
                          vm.moveCardToDone(card.id);
                        }
                        if (vm.autoQueue) {
                          $timeout(function () { vm.processQueue(); }, 500);
                        }
                      }

                      if (!incomplete && card.autoPr && card.prStatus && card.prStatus.branch) {
                        card.prStatus.status = 'creating-pr';
                        pushAgentLog('info', 'Creating PR for branch ' + card.prStatus.branch + '...');
                        $http.post('/api/pr/finish', {
                          projectPath: proj,
                          cardId: card.id,
                          cardText: card.text,
                          branchName: card.prStatus.branch,
                          summary: finalSummary,
                          originalBranch: card.prStatus.originalBranch
                        }).then(function (prResp) {
                          var prData = prResp.data;
                          if (prData && prData.success) {
                            card.prStatus = { status: 'pr-created', branch: card.prStatus.branch, prUrl: prData.prUrl };
                            pushAgentLog('info', 'PR created: ' + (prData.prUrl || 'Check your repository'));
                          } else {
                            card.prStatus = { status: 'error', error: (prData && prData.error) || 'PR creation failed', branch: card.prStatus.branch };
                            pushAgentLog('warn', 'PR creation: ' + card.prStatus.error);
                          }
                          finishCard();
                        }, function (err) {
                          card.prStatus = { status: 'error', error: err.statusText || 'PR failed', branch: card.prStatus.branch };
                          pushAgentLog('warn', 'PR creation failed: ' + card.prStatus.error);
                          finishCard();
                        });
                      } else {
                        if (incomplete) {
                          pushAgentLog('warn', 'Card kept in Doing — no files were modified');
                        }
                        finishCard();
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
              try { if (!$scope.$$phase) $scope.$digest(); } catch (e) { /* infdig guard */ }
              readNext();
              }).catch(function (readErr) {
              if (readErr && readErr.name === 'AbortError') return;
              vm.streamingActive = false;
              resumeTerminalPolling();
              vm.agentResult = { error: 'Stream read error: ' + (readErr && readErr.message || readErr) };
              try { if (!$scope.$$phase) $scope.$digest(); } catch (e) { /* infdig guard */ }
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
          $scope.$applyAsync();
        });
      }

      // PR: create branch before starting agent
      if (card.autoPr && proj) {
        pushAgentLog('info', 'Creating PR branch...');
        $http.post('/api/pr/start', { projectPath: proj, cardId: card.id, cardText: card.text })
          .then(function (resp) {
            var d = resp.data;
            if (d && d.success) {
              card.prStatus = { status: 'branch-created', branch: d.branchName, originalBranch: d.originalBranch };
              pushAgentLog('info', 'PR branch: ' + card.prStatus.branch);
            } else {
              card.prStatus = { status: 'error', error: (d && d.error) || 'Branch creation failed' };
              pushAgentLog('warn', 'PR branch failed: ' + card.prStatus.error);
            }
            startAgent();
          }, function (err) {
            card.prStatus = { status: 'error', error: err.statusText || 'Network error' };
            pushAgentLog('warn', 'PR branch failed: ' + card.prStatus.error);
            startAgent();
          });
      } else {
        startAgent();
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
      fetch('/api/terminal/output').then(function (r) { return r.json(); }).then(function (data) {
        var newOutput = (data && data.output) || '';
        if (newOutput !== vm.terminalOutput) {
          vm.terminalOutput = newOutput;
          if (!$scope.$$phase) $scope.$digest();
          // Scroll to bottom of terminal output
          $timeout(function () {
            var terminalOutput = document.querySelector('.terminalOutput');
            if (terminalOutput) {
              terminalOutput.scrollTop = terminalOutput.scrollHeight;
            }
          }, 0, false);
        }
      });
    };

    vm.refreshTerminalApprovals = function () {
      fetch('/api/terminal/approvals/pending').then(function (r) { return r.json(); }).then(function (data) {
        vm.pendingTerminalApprovals = (data && data.approvals) || [];
        if (!$scope.$$phase) $scope.$digest();
      }, function () {
        vm.pendingTerminalApprovals = [];
        if (!$scope.$$phase) $scope.$digest();
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

    vm.dismissContextReview = function () {
      if (vm.contextReviewTimer) { $interval.cancel(vm.contextReviewTimer); vm.contextReviewTimer = null; }
      if (vm.contextReviewAutoConfirm) { $timeout.cancel(vm.contextReviewAutoConfirm); vm.contextReviewAutoConfirm = null; }
      vm.pendingContextReview = null;
      vm._contextReviewSubmitted = false;
    };

    vm.confirmContextReview = function () {
      if (!vm.pendingContextReview || vm._contextReviewSubmitted) return;
      vm._contextReviewSubmitted = true;
      if (vm.contextReviewTimer) { $interval.cancel(vm.contextReviewTimer); vm.contextReviewTimer = null; }
      if (vm.contextReviewAutoConfirm) { $timeout.cancel(vm.contextReviewAutoConfirm); vm.contextReviewAutoConfirm = null; }

      var selected = [];
      var files = vm.pendingContextReview.files;
      if (files && files.length) {
        files.forEach(function (f) {
          if (f.keep !== false) selected.push(f.path);
        });
      }
      $http.post('/api/agent/context-review/confirm', { id: vm.pendingContextReview.id, files: selected }).then(function () {
        var card = findCardById(vm.activeCardId);
        if (card && selected.length > 0) {
          card.confirmedContextFiles = selected;
          var existing = Array.isArray(card.attached) ? card.attached : (card.attached ? [card.attached] : []);
          selected.forEach(function (f) {
            if (existing.indexOf(f) === -1) existing.push(f);
          });
          card.attached = existing;
          vm.saveCards();
        }
        vm.pendingContextReview = null;
        vm._contextReviewSubmitted = false;
      }, function (err) {
        vm._contextReviewSubmitted = false;
        pushAgentLog('error', 'Failed to submit context review: ' + (err.statusText || err));
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
      vm.activeCardId = null;
      vm.activeCardIds = new Set();
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
    var _terminalInterval = $interval(vm.refreshTerminal, 3000, 0, false);
    var _approvalInterval = $interval(vm.refreshTerminalApprovals, 1500, 0, false);

    function pauseTerminalPolling() {
      if (_terminalInterval) { $interval.cancel(_terminalInterval); _terminalInterval = null; }
      if (_approvalInterval) { $interval.cancel(_approvalInterval); _approvalInterval = null; }
    }
    function resumeTerminalPolling() {
      if (!_terminalInterval) _terminalInterval = $interval(vm.refreshTerminal, 3000, 0, false);
      if (!_approvalInterval) _approvalInterval = $interval(vm.refreshTerminalApprovals, 1500, 0, false);
    }
  }]);    