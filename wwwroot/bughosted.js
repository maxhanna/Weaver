// bughosted.mixin.js
angular.module('kanbanApp')
    .factory('BugHostedMixin', ['$http', '$interval', '$timeout', function ($http, $interval, $timeout) {
        var _bhHeartbeatFailCount = 0, _bhHeartbeatTimer = null, _bhEditorSyncTimer = null, _bhEventSource = null, _bhCommandTimer = null, _bhTimerRunning = false, _lastSyncedEditorState = null, _isSyncingData = false;

        function uid() { return Math.random().toString(36).slice(2, 9); }
        function findCardColumn(vm, cardId) {
            if (!cardId || !vm.state) return null;
            var cols = ['todo', 'doing', 'done', 'archived', 'selfImproving'];
            for (var i = 0; i < cols.length; i++) {
                var cards = vm.state[cols[i]] || [];
                for (var j = 0; j < cards.length; j++) { if (cards[j].id === cardId) return cols[i]; }
            }
            return null;
        }

        return {
            init: function (vm, $scope) {
                vm.bughostedUsername = ''; vm.bughostedPassword = ''; vm.bughostedHeartbeatEnabled = false;
                vm.bughostedClientId = ''; vm.bughostedStatus = 'disconnected'; vm.bughostedTesting = false;
                vm.bughostedTestResult = ''; vm.bughostedTestError = ''; vm.remoteCommands = [];

                function buildHeartbeatPayload() {
                    return {
                        clientId: vm.bughostedClientId,
                        kanbanData: JSON.stringify({ projects: (vm.projects || []).map(function (p) { return { Name: p.Name, Path: p.Path, Description: p.Description, BuildCommands: p.BuildCommands }; }), state: vm.state, agentActive: vm.streamingActive || false, agentPhase: vm.streamingPhase || '', agentThinking: vm.streamingThinking || '', agentSummary: vm.streamingSummary || '', activeCardId: vm.activeCardId || null, activeCardText: vm.activeCardText || '', calendarCards: vm.calCards || [] }),
                        settings: JSON.stringify({ llamaUrl: vm.llamaUrl, llamaModel: vm.llamaModel, terminalApprovalMode: vm.terminalApprovalMode, defaultProject: vm.defaultProject || vm.selectedProject, showTerminal: vm.showTerminal, showAI: vm.showAI, showIDE: vm.showIDE, showKanban: vm.showKanban, showCalendar: vm.showCalendar, bughostedHeartbeatEnabled: vm.bughostedHeartbeatEnabled, bughostedUsername: vm.bughostedUsername, bughostedPassword: vm.bughostedPassword })
                    };
                }

                vm.bughostedLogin = function () {
                    if (!vm.bughostedUsername || !vm.bughostedPassword) return;
                    vm.bughostedStatus = 'connecting';
                    $http.post('/api/bughosted/login', { Username: vm.bughostedUsername, Password: vm.bughostedPassword }).then(function (resp) {
                        vm.bughostedClientId = resp.data.clientId; vm.bughostedStatus = 'connected'; startBughostedHeartbeat(); startBughostedCommandPolling();
                    }, function () { vm.bughostedStatus = 'error'; vm.bughostedClientId = ''; });
                };

                vm.bughostedLogout = function () {
                    if (vm.bughostedClientId) $http.post('/api/bughosted/logout', { clientId: vm.bughostedClientId });
                    vm.bughostedClientId = ''; vm.bughostedStatus = 'disconnected'; stopBughostedHeartbeat(); stopBughostedCommandPolling();
                };

                vm.bughostedToggle = function () { (vm.bughostedStatus === 'connected' || vm.bughostedClientId) ? vm.bughostedLogout() : vm.bughostedLogin(); };
                vm.bughostedTestConnection = function () {
                    if (!vm.bughostedUsername || !vm.bughostedPassword) return; vm.bughostedTesting = true; vm.bughostedTestResult = '';
                    $http.post('/api/bughosted/test', { Username: vm.bughostedUsername, Password: vm.bughostedPassword }).then(function (resp) {
                        if (resp.data.success) vm.bughostedTestResult = 'ok'; else { vm.bughostedTestResult = 'fail'; vm.bughostedTestError = resp.data.error || 'HTTP ' + resp.data.statusCode; }
                        vm.bughostedTesting = false;
                    }, function () { vm.bughostedTestResult = 'fail'; vm.bughostedTestError = 'Cannot reach server'; vm.bughostedTesting = false; });
                };
                vm.bughostedForceReconnect = function () { vm.bughostedLogout(); _bhHeartbeatFailCount = 0; $timeout(function () { vm.bughostedLogin(); }, 300); };

                vm.syncEditorState = function () {
                    if (!vm.bughostedClientId || vm.bughostedStatus !== 'connected' || _isSyncingData || vm.shuttingDown) return;
                    _isSyncingData = true;
                    var data = buildHeartbeatPayload();
                    $http.post('/api/bughosted/heartbeat', data).then(function () { _bhHeartbeatFailCount = 0; vm.bughostedStatus = 'connected'; _isSyncingData = false; }, function () { _bhHeartbeatFailCount++; if (_bhHeartbeatFailCount >= 3) vm.bughostedStatus = 'error'; _isSyncingData = false; });
                }; 
                
                function startBughostedHeartbeat() {
                    stopBughostedHeartbeat();
                    _bhHeartbeatTimer = $interval(function () {
                        if (!vm.bughostedClientId || vm.bughostedStatus !== 'connected' || vm.shuttingDown) return;
                        _lastSyncedEditorState = null; var data = buildHeartbeatPayload();
                        $http.post('/api/bughosted/heartbeat', data).then(function () { if (vm.shuttingDown) return; _bhHeartbeatFailCount = 0; vm.bughostedStatus = 'connected'; }, function () { if (vm.shuttingDown) return; _bhHeartbeatFailCount++; if (_bhHeartbeatFailCount >= 3) vm.bughostedStatus = 'error'; });
                    }, 30000, 0, false);
                    _bhEditorSyncTimer = $interval(function () { if (vm.bughostedClientId && vm.bughostedStatus === 'connected' && !vm.shuttingDown) vm.syncEditorState(); }, 3000, 0, false);
                }
                function stopBughostedHeartbeat() {
                    if (_bhHeartbeatTimer) { $interval.cancel(_bhHeartbeatTimer); _bhHeartbeatTimer = null; }
                    if (_bhEditorSyncTimer) { $interval.cancel(_bhEditorSyncTimer); _bhEditorSyncTimer = null; }
                }

                function receiveCommand(cmd) {
                    if (cmd.parameters && !cmd.params) { try { cmd.params = JSON.parse(cmd.parameters); } catch (e) { cmd.params = {}; } }
                    if (!vm.remoteCommands) vm.remoteCommands = [];
                    var existing = vm.remoteCommands.find(function (c) { return c.id === cmd.id; });
                    if (!existing && cmd.command) { vm.remoteCommands.push(cmd); vm.executeRemoteCommand(cmd); }
                }

                function startBughostedCommandPolling() {
                    if (vm.destroyed) return; stopBughostedCommandPolling();
                    var clientId = vm.bughostedClientId; if (!clientId || vm.bughostedStatus !== 'connected') return;
                    try {
                        var es = new EventSource('/api/bughosted/events?clientId=' + encodeURIComponent(clientId));
                        es.addEventListener('command', function (e) { try { var cmd = JSON.parse(e.data); $timeout(function () { receiveCommand(cmd); }, 0); } catch (ex) { } });
                        es.onerror = function () { es.close(); _bhEventSource = null; startPollingFallback(); };
                        _bhEventSource = es;
                    } catch (e) { startPollingFallback(); }
                }
                function startPollingFallback() {
                    if (vm.destroyed || _bhCommandTimer) return;
                    _bhCommandTimer = $interval(function () {
                        if (vm.destroyed || _bhTimerRunning || !vm.bughostedClientId || vm.bughostedStatus !== 'connected' || vm.shuttingDown) return;
                        _bhTimerRunning = true;
                        $http.get('/api/bughosted/commands?clientId=' + encodeURIComponent(vm.bughostedClientId)).then(function (resp) {
                            _bhTimerRunning = false;
                            if (resp && resp.data && resp.data.length > 0) resp.data.forEach(function (cmd) { $timeout(function () { receiveCommand(cmd); }, 0); });
                        }).catch(function () { _bhTimerRunning = false; });
                    }, 5000, 0, false);
                }
                function stopBughostedCommandPolling() {
                    if (_bhEventSource) { _bhEventSource.close(); _bhEventSource = null; }
                    if (_bhCommandTimer) { $interval.cancel(_bhCommandTimer); _bhCommandTimer = null; }
                }

                vm.executeRemoteCommand = function (cmd) {
                    // [Kept identical to original logic, utilizing vm.* methods like vm.moveCard, vm.saveCards, vm.executeAgent]
                    if (cmd.command === 'executeTask' && cmd.params && cmd.params.text) {
                        var card = { id: uid(), text: cmd.params.text, filePath: cmd.params.project || vm.selectedProject, createdAt: new Date().toISOString(), priority: cmd.params.priority || 'medium', attached: [], selfImproving: false, isDecomposing: false };
                        vm.state.todo.push(card); vm.saveCards();
                    } else if (cmd.command === 'addCard') {
                        var card = { id: cmd.params.cardId || uid(), text: cmd.params.text || cmd.params.title || '', filePath: cmd.params.project || vm.selectedProject, createdAt: new Date().toISOString(), priority: cmd.params.priority || 'medium', attached: [], selfImproving: false, isDecomposing: false };
                        vm.state.todo.push(card); vm.saveCards();
                    } else if (cmd.command === 'moveCard' && cmd.params) {
                        var fromCol = findCardColumn(vm, cmd.params.cardId); if (fromCol && cmd.params.status && fromCol !== cmd.params.status) vm.moveCard(cmd.params.cardId, fromCol, cmd.params.status);
                    } else if (cmd.command === 'updateCard' && cmd.params) {
                        var c = vm.findCardById ? vm.findCardById(cmd.params.cardId) : null; if (c) { if (cmd.params.text) c.text = cmd.params.text; if (cmd.params.priority) c.priority = cmd.params.priority; if (cmd.params.attached !== undefined) c.attached = cmd.params.attached; if (cmd.params.autoPr !== undefined) c.autoPr = cmd.params.autoPr; vm.saveCards(); }
                    } else if (cmd.command === 'archiveCard' && cmd.params) {
                        var col = findCardColumn(vm, cmd.params.cardId) || 'done'; vm.archiveCard(cmd.params.cardId, col);
                    } else if (cmd.command === 'startAgent' && cmd.params) {
                        var c = vm.findCardById ? vm.findCardById(cmd.params.cardId) : null; if (c && !vm.streamingActive) vm.executeAgent(c);
                    } else if (cmd.command === 'stopAgent') {
                        var activeCard = vm.findCardById ? vm.findCardById(vm.activeCardId) : null; vm.stopAgent && vm.stopAgent(activeCard);
                    } else if (cmd.command === 'updateSettings' && cmd.params) {
                        if (cmd.params.llamaUrl !== undefined) vm.llamaUrl = cmd.params.llamaUrl;
                        if (cmd.params.llamaModel !== undefined) vm.llamaModel = cmd.params.llamaModel;
                        if (cmd.params.terminalApprovalMode !== undefined) vm.terminalApprovalMode = cmd.params.terminalApprovalMode;
                        vm.saveSettings();
                    }
                    $http.post('/api/bughosted/commands/ack', { clientId: vm.bughostedClientId, commandId: cmd.id, status: 'executed', result: 'ok' });
                };

                vm.stopBughostedTimers = function () { stopBughostedHeartbeat(); stopBughostedCommandPolling(); };
                $scope.$on('$destroy', function () { stopBughostedHeartbeat(); stopBughostedCommandPolling(); });
            }
        };
    }]);