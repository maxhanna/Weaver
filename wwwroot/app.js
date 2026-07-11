// app.js
angular.module('kanbanApp', [])
  .filter('formatNumber', function () {
    return function (input) {
      if (input === null || input === undefined) return '';
      return input.toString().replace(/\B(?=(\d{3})+(?!\d))/g, ',');
    };
  })
  .controller('MainCtrl', [
    '$http', '$interval', '$window', '$scope', '$timeout',
    'KanbanMixin', 'CalendarMixin', 'IDEMixin',
    'SettingsMixin', 'BugHostedMixin', 'TerminalMixin',
    'AgentMixin', 'FilePickerMixin',
    function ($http, $interval, $window, $scope, $timeout,
      KanbanMixin, CalendarMixin, IDEMixin,
      SettingsMixin, BugHostedMixin, TerminalMixin,
      AgentMixin, FilePickerMixin) {

      const vm = this;

      // === Global UI State ===
      vm.faqs = [
        { question: 'How do I get started?', answer: 'To get started, simply create a new project and begin adding tasks to your Kanban board.', expanded: false },
        { question: 'Can I collaborate with others?', answer: 'Yes, you can invite team members to collaborate on projects and share Kanban boards.', expanded: false },
        { question: 'How do I export my data?', answer: 'You can export your Kanban data as JSON by clicking the export button in the settings panel.', expanded: false }
      ];

      vm.showKanban = true;
      vm.showCalendar = false;
      vm.showTodo = true;
      vm.showDoing = true;
      vm.showDone = true;
      vm.showArchived = false;
      vm.showSelfImproving = false;
      vm.isSearchResult = false;

      // === Global UI Methods ===
      vm.playSound = function () {
        var audio = new Audio('/wwwroot/zen.mp3');
        audio.play();
      };

      vm.showNotification = async function (message) {
        if ("Notification" in window) {
          let permission = Notification.permission;
          if (permission === "granted") {
            new Notification("Weaver", { body: message });
            return;
          }
          if (permission === "default") {
            try {
              permission = await Notification.requestPermission();
              if (permission === "granted") {
                new Notification("Weaver", { body: message });
                return;
              }
            } catch (e) { console.warn("Notification permission request failed:", e); }
          }
        }
        alert(message); // Fallback toast
      };

      vm.sendSystemToast = function () {
        if (navigator.userAgent.indexOf('Win') !== -1) {
          vm.showNotification('Done');
          vm.playSound();
        }
      };

      vm.exportKanbanData = function () {
        const data = JSON.stringify(vm.state);
        alert(data);
        return data;
      };

      // === Global Log & Fonts ===
      vm.agentActivityLog = [];
      vm.agentActivityLogLength = 0;
      vm.logFontSize = 18;
      vm.llmFontSize = 14;
      vm.planFontSize = 14;

      vm.scrollToBottom = function () {
        $timeout(function () {
          var logContainer = document.querySelector('.log-entries');
          if (logContainer) logContainer.scrollTop = logContainer.scrollHeight;
        }, 10, false);
      };

      vm.increaseLogFont = function () { vm.logFontSize = Math.min(vm.logFontSize + 2, 32); };
      vm.decreaseLogFont = function () { vm.logFontSize = Math.max(vm.logFontSize - 2, 6); };
      vm.increaseLlmFont = function () { vm.llmFontSize = Math.min(vm.llmFontSize + 2, 32); };
      vm.decreaseLlmFont = function () { vm.llmFontSize = Math.max(vm.llmFontSize - 2, 6); };
      vm.increasePlanFont = function () { vm.planFontSize = Math.min(vm.planFontSize + 2, 32); };
      vm.decreasePlanFont = function () { vm.planFontSize = Math.max(vm.planFontSize - 2, 6); };

      vm.addLogEntry = function (entry) {
        if (vm.agentActivityLogLength > 0) {
          var lastEntry = vm.agentActivityLog[vm.agentActivityLogLength - 1];
          if ((lastEntry.type === entry.type && lastEntry.message === entry.message) || lastEntry.timestamp === entry.timestamp) return;
        }
        vm.agentActivityLog.push(entry);
        vm.agentActivityLogLength = vm.agentActivityLog.length;
      };

      // === Initialize Mixins ===
      // Order matters: Settings/State first, then features, then Agent
      SettingsMixin.init(vm, $scope);
      KanbanMixin.init(vm, $scope);
      CalendarMixin.init(vm, $scope);
      IDEMixin.init(vm, $scope);
      TerminalMixin.init(vm, $scope);
      FilePickerMixin.init(vm, $scope);
      AgentMixin.init(vm, $scope);
      BugHostedMixin.init(vm, $scope);

      // === Global Init Calls ===
      if (vm.emailAccounts.length === 0) vm.addEmailAccount();
      vm.loadConfig();
      vm.countArchivedCards();
      vm.startCalendarProcessing();

      // Global Keybindings
      document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape' && vm.deleteCardConfirm && vm.deleteCardConfirm.show) {
          if (vm.closeSettingsPanel) vm.closeSettingsPanel();
          if (vm.closeEditProjectsPanel) vm.closeEditProjectsPanel();
          if (vm.closeFilePicker) vm.closeFilePicker();
          if (vm.closeDeleteCardConfirm) vm.closeDeleteCardConfirm();
        }
      });

      $scope.$on('$destroy', function () {
        vm.destroyed = true;
        if (vm.abortController) vm.abortController.abort();
      });
    }
  ]);