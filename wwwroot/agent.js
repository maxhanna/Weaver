// agent.mixin.js
angular.module('kanbanApp')
    .factory('AgentMixin', ['$http', '$timeout', '$interval', '$window', function ($http, $timeout, $interval, $window) {
        var _lastLogKey = '';

        function uid() { return Math.random().toString(36).slice(2, 9); }

        function normalizeStepStatus(status) {
            if (status === 'written' || status === 'ok' || status === 'created' || status === 'modified') return 'done';
            return status || 'pending';
        }
        function normalizeStep(step) { if (!step) return step; step.status = normalizeStepStatus(step.status); return step; }

        function pushAgentLog(vm, level, message, detail) {
            if (!message || level === 'status') return;
            try {
                function normalise(s) { return (s || '').replace(/\d+/g, '#'); }
                var recentDupe = vm.agentActivityLog.length > 0 && vm.agentActivityLog.slice(-3).some(function (e) { return e.level === level && normalise(e.message) === normalise(message); });
                if (recentDupe && level !== 'error' && level !== 'warn') return;
                var entry = { ts: new Date().toLocaleTimeString(), level: level || 'info', message: message, detail: detail };
                vm.agentActivityLog.push(entry); vm.agentActivityLogLength = vm.agentActivityLog.length;
                if (vm.agentActivityLogLength > 100) vm.agentActivityLog.shift();
                if (vm.scrollToBottom) vm.scrollToBottom();
            } catch (e) { }
        }

        function refreshFilesEditedFromSteps(vm) {
            var seen = {};
            vm.streamingFilesEdited = vm.streamingSteps.filter(function (s) { return (s.type === 'edit' || s.type === 'rename') && s.status === 'done' && s.path; }).filter(function (s) { var already = seen[s.path]; seen[s.path] = true; return !already; }).map(function (s) { var info = { path: s.path, editAction: s.editAction, linesAdded: s.linesAdded, linesRemoved: s.linesRemoved }; if (s.type === 'rename') info.editAction = 'renamed → ' + (s.toPath || ''); return info; });
        }

        function reconcilePlanItems(vm, $scope, $timeout) {
            if (!vm.planItems || !vm.planItems.length) return;
            var changed = false;
            vm.planItems.forEach(function (item) {
                if (item.done) return;
                var doneSteps = vm.streamingSteps.filter(function (s) {
                    if (s.status !== 'done' && s.status !== 'skipped' && s.status !== 'error') return false;
                    if (s.planItemIndex !== undefined && s.planItemIndex !== null) return s.planItemIndex === item.index;
                    return (s.type === 'edit' || s.type === 'rename') && s.path && item.file && s.path.replace(/\\/g, '/').toLowerCase() === item.file.toLowerCase();
                });
                if (doneSteps.length > 0) { item.done = true; changed = true; }
            });
            var activeCard = vm.findCardById ? vm.findCardById(vm.activeCardId) : null;
            if (activeCard && activeCard._plan && changed) activeCard._plan.items = angular.copy(vm.planItems);
            if (changed && vm.saveCards) {
                if (vm._saveCardsTimer) $timeout.cancel(vm._saveCardsTimer);
                vm._saveCardsTimer = $timeout(function () { if (vm.saveCards) vm.saveCards(); }, 500);
            }
        }

        function upsertStreamingStep(vm, parsed, $scope, $timeout) {
            normalizeStep(parsed);
            var existing = vm.streamingSteps.find(function (s) { return s.index === parsed.index; });
            if (existing) angular.extend(existing, parsed); else vm.streamingSteps.push(parsed);
            vm.streamingSteps.sort(function (a, b) { return (a.index || 0) - (b.index || 0); });
            if (parsed.status === 'running') vm.activeStepIndex = parsed.index;
            else { var running = vm.streamingSteps.find(function (s) { return s.status === 'running'; }); vm.activeStepIndex = running ? running.index : null; }
            refreshFilesEditedFromSteps(vm);
        }

        return {
            init: function (vm, $scope) {
                // State
                vm.aiPrompt = ''; vm.aiResponse = ''; vm.activeCardText = ''; vm.activeCardId = null;
                vm.activeCardIds = new Set(); vm.aiChatMessages = []; vm.aiChatInput = ''; vm.aiChatLoading = false; vm.chatMode = 'ask';
                vm.streamingActive = false; vm.streamingThinking = ''; vm.streamingSummary = ''; vm._agentStopped = false; vm.streamingPhase = '';
                vm.streamingContextSize = 0; vm.streamingSteps = []; vm.streamingFilesEdited = []; vm.streamingTokenBuffer = '';
                vm.streamingStableCount = 0; vm.activeStepIndex = null; vm.agentResult = null; vm.steeringContext = ''; vm.clarificationReply = '';
                vm.abortController = new AbortController(); vm.planItems = []; vm.cohesionIssues = []; vm.cohesionFile = '';
                vm.pendingContextReview = null; vm.contextReviewCountdown = 0; vm.contextReviewTimer = null;
                vm.buildTools = [
                    { name: 'Ping', icon: '📡', desc: 'Check host connectivity (TCP/ping/HTTP)', hint: 'ping google.com -n 4' },
                    { name: 'Install Package', icon: '📦', desc: 'Install a NuGet/npm/pip package', hint: 'install package SonarAnalyzer.CSharp' },
                    { name: 'Build', icon: '🔨', desc: 'Run build verification', hint: 'build the project' },
                    { name: 'Full Agent', icon: '🤖', desc: 'Run the full agent pipeline', hint: 'refactor the login page' }
                ];

                // Benchmarks State
                vm.benchmarkScores = []; vm.benchmarkRunning = false; vm.benchmarkLevel = null; vm.selectedBenchmarkScore = null; vm.benchmarkPlanNames = {};

                // Methods
                vm.useToolHint = function (hint) { vm.aiChatInput = hint; var el = document.querySelector('.ai-chat-body input'); if (el) el.focus(); };
                vm.toggleChatMode = function () { vm.chatMode = vm.chatMode === 'ask' ? 'build' : 'ask'; };

                vm.logFileSizeAndTokens = function (filePath, content) {
                    if (!filePath || !content) return; const fileSize = content.length; const tokenCount = Math.ceil(fileSize / 4);
                    if (vm.addLogEntry) vm.addLogEntry({ type: 'debug', message: `File: ${filePath} | Size: ${fileSize} chars | Tokens: ~${tokenCount}` });
                };

                vm.executeAgent = function (card, isAutoRestart) {
                    if (!card || vm.streamingActive || !card.text) return;
                    var proj = card.filePath || vm.selectedProject; if (!proj) return $window.alert('No project assigned');
                    try {
                        if (!isAutoRestart) card._agentIteration = 0;
                        delete card.agentAnalysis; delete card.agentLog;

                        function startAgent() {
                            vm.agentResult = null; vm._agentStopped = false; vm.aiResponse = ''; vm.streamingThinking = ''; vm.streamingSummary = '';
                            vm.streamingPhase = ''; vm.streamingContextSize = 0; vm.streamingTokenBuffer = ''; vm.streamingStableCount = 0;
                            vm.streamingSteps = []; vm.streamingFilesEdited = []; vm.planItems = []; vm.cohesionIssues = []; vm.cohesionFile = '';
                            vm.agentActivityLog = []; vm.activeStepIndex = null; vm.streamingActive = true; vm.pauseTerminalPolling();

                            pushAgentLog(vm, 'info', 'Agent started', { project: proj, task: card.text });
                            vm.activeCardText = card.text; vm._agentStartTime = Date.now();
                            var files = Array.isArray(card.attached) ? card.attached : (card.attached ? [card.attached] : []);
                            var payload = { prompt: card.text, project: proj, files: files, maxIterations: 5, maxStepsPerBatch: 8, steeringContext: vm.steeringContext || '', selfImproving: card.selfImproving || false, isDecomposing: card.isDecomposing || false, createTests: card.createTests || false, cardId: card.id, isBenchmark: card._benchmark || false, buildCommands: vm.getProjectBuildCommands(proj) || null };

                            vm.moveCardToDoing(card.id); vm.activeCardId = card.id; vm.activeCardIds.add(card.id);
                            var localAbortController = vm.abortController;

                            fetch('/api/agent/execute-stream', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload), signal: localAbortController.signal })
                                .then(function (response) {
                                    if (!response.ok) { vm.streamingActive = false; vm.resumeTerminalPolling(); vm.agentResult = { error: 'Server error: ' + response.status }; $scope.$applyAsync(); return; }
                                    var reader = response.body.getReader(); var decoder = new TextDecoder(); var buffer = '';
                                    function readNext() {
                                        reader.read().then(function (result) {
                                            if (result.done) { if (!vm.streamingActive) return; vm.streamingActive = false; vm.resumeTerminalPolling(); $scope.$applyAsync(); return; }
                                            buffer += decoder.decode(result.value, { stream: true }); var parts = buffer.split('\n\n'); buffer = parts.pop();

                                            $scope.$applyAsync(function () {
                                                for (var p = 0; p < parts.length; p++) {
                                                    var lines = parts[p].split('\n'); var eventName = ''; var data = ''; var eventLineFound = false;
                                                    for (var l = 0; l < lines.length; l++) {
                                                        if (!eventLineFound && lines[l].startsWith('event: ')) { eventName = lines[l].slice(7).trim(); eventLineFound = true; }
                                                        else if (lines[l].startsWith('data: ')) { if (data) data += '\n'; data += lines[l].slice(6); }
                                                    }
                                                    data = data.trimEnd ? data.trimEnd() : data.replace(/\s+$/, '');

                                                    if (eventName) {
                                                        var parsed = null; try { parsed = JSON.parse(data); } catch (e) { }
                                                        switch (eventName) {
                                                            case 'log':
                                                                if (parsed) pushAgentLog(vm, parsed.level, parsed.message, parsed.detail);
                                                                break;
                                                            case 'phase':
                                                                if (parsed && parsed.message) {
                                                                    vm.streamingPhase = parsed.message;
                                                                    if (parsed.message !== vm.lastPhaseLogged) { vm.lastPhaseLogged = parsed.message; pushAgentLog(vm, 'phase', parsed.message); }
                                                                } else if (parsed && parsed.phase) { vm.streamingPhase = parsed.phase; }
                                                                if (parsed && parsed.contextSize) { vm.streamingContextSize = parsed.contextSize; }
                                                                break;
                                                            case 'status':
                                                                if (parsed && parsed.message) vm.streamingPhase = parsed.message;
                                                                break;
                                                            case 'token':
                                                                if (parsed && parsed.token) {
                                                                    vm.streamingTokenBuffer += parsed.token;
                                                                    if (vm._streamingLengthTimer) { $timeout.cancel(vm._streamingLengthTimer); }
                                                                    vm._streamingLengthTimer = $timeout(function () { vm.streamingStableCount = vm.streamingTokenBuffer.length; }, 100);
                                                                    if (vm.resolveStreams) { var buf = vm.resolveStreams; if (buf && buf.length) buf[buf.length - 1].content += parsed.token; }
                                                                }
                                                                break;
                                                            case 'thinking':
                                                                if (parsed && parsed.text) {
                                                                    vm.streamingThinking = parsed.text;
                                                                    pushAgentLog(vm, 'think', 'Plan updated (Plan length: ' + parsed.text.length + ' chars)', { text: parsed.text });
                                                                }
                                                                break;
                                                            case 'summary':
                                                                if (parsed && parsed.text) { vm.streamingSummary = parsed.text; pushAgentLog(vm, 'summary', parsed.text); }
                                                                break;
                                                            case 'meta-plan':
                                                                if (parsed) {
                                                                    vm.streamingMetaPlan = { summary: parsed.summary, complexity: parsed.complexity, subPlans: parsed.subPlans.map(function (sp) { return { id: sp.id, title: sp.title, description: sp.description, files: sp.files || [], contextNote: sp.contextNote, done: sp.done || false }; }) };
                                                                    pushAgentLog(vm, 'info', '🧠 Meta-Plan: ' + parsed.summary + ' (Complexity: ' + parsed.complexity + '/10)');
                                                                }
                                                                break;
                                                            case 'meta-plan-step-updated':
                                                                if (parsed && vm.streamingMetaPlan && vm.streamingMetaPlan.subPlans) {
                                                                    var sp = vm.streamingMetaPlan.subPlans.find(function (s) { return s.id === parsed.subPlanId; });
                                                                    if (sp) sp.done = parsed.done;
                                                                }
                                                                break;
                                                            case 'plan':
                                                                if (parsed && parsed.items && parsed.items.length) {
                                                                    vm.planItems = parsed.items.map(function (item, i) {
                                                                        return { index: i, file: item.File || item.file || '?', change: item.Change || item.change || '', priority: item.Priority || item.priority || i + 1, line: item.Line || item.line || 0, done: item.done || false, oldString: item.OldString || item.oldString || '', newString: item.NewString || item.newString || '' };
                                                                    });
                                                                    if (parsed.thinking) vm.streamingThinking = parsed.thinking;
                                                                    if (parsed.summary) vm.streamingSummary = parsed.summary;
                                                                    pushAgentLog(vm, 'info', '📋 Plan: ' + parsed.summary + ' (' + parsed.items.length + ' steps)', { itemCount: parsed.items.length, score: parsed.score });

                                                                    var activeCard = vm.findCardById(vm.activeCardId);
                                                                    if (activeCard) {
                                                                        activeCard._plan = { items: angular.copy(vm.planItems), summary: parsed.summary, score: parsed.score };
                                                                        vm.saveCards();
                                                                    }
                                                                }
                                                                break;
                                                            case 'edit-resolve':
                                                                if (vm.resolveStreams) vm.resolveStreams.push({ content: '' });
                                                                break;
                                                            case 'show':
                                                                if (parsed && parsed.text) { vm.aiResponse = parsed.text; pushAgentLog(vm, 'info', '📄 ' + parsed.text); }
                                                                break;
                                                            case 'clarification':
                                                                if (parsed && parsed.question) { vm.aiResponse = parsed.question; pushAgentLog(vm, 'warn', 'Clarification needed', { question: parsed.question }); }
                                                                break;
                                                            case 'refresh':
                                                                if (parsed && parsed.target === 'boarddata' && vm.refreshBoardData) vm.refreshBoardData(parsed);
                                                                break;
                                                            case 'step':
                                                                if (parsed) {
                                                                    upsertStreamingStep(vm, parsed, $scope, $timeout);
                                                                    reconcilePlanItems(vm, $scope, $timeout);
                                                                    if (parsed.message === 'Cancelled by user' && parsed.planItemIndex !== undefined && vm.planItems) {
                                                                        var cancelledItem = vm.planItems.find(function (pi) { return pi.index === parsed.planItemIndex; });
                                                                        if (cancelledItem) cancelledItem.cancelled = true;
                                                                    }
                                                                    if (parsed.status === 'running') {
                                                                        pushAgentLog(vm, 'step', '▶ ' + parsed.type + ': ' + (parsed.description || parsed.path || parsed.command || ''));
                                                                    } else if (parsed.status === 'error') {
                                                                        pushAgentLog(vm, 'error', '✕ ' + parsed.type + ': ' + (parsed.error || parsed.description || ''));
                                                                    } else if (parsed.skipped) {
                                                                        pushAgentLog(vm, 'info', '⏭ ' + parsed.type + ': ' + (parsed.description || parsed.path || '') + ' (already done)');
                                                                    }
                                                                }
                                                                break;
                                                            case 'context-review':
                                                                try {
                                                                    if (parsed && parsed.id && parsed.files) {
                                                                        const ctx = parsed; ctx.files.forEach(function (f) { f.keep = true; });
                                                                        vm.pendingContextReview = ctx; vm._contextReviewSubmitted = false; vm.contextReviewCountdown = 5;
                                                                        pushAgentLog(vm, 'phase', '📋 Context review — ' + ctx.files.length + ' file(s) discovered, auto-confirm in 5s');
                                                                        if (vm.contextReviewTimer) $interval.cancel(vm.contextReviewTimer);
                                                                        if (vm.contextReviewAutoConfirm) $timeout.cancel(vm.contextReviewAutoConfirm);
                                                                        vm.contextReviewTimer = $interval(function () { vm.contextReviewCountdown--; if (vm.contextReviewCountdown < 0) vm.contextReviewCountdown = 0; }, 1000, 5);
                                                                        vm.contextReviewAutoConfirm = $timeout(function () { if (!vm._contextReviewSubmitted && vm.pendingContextReview) vm.confirmContextReview(); }, 5000);
                                                                    }
                                                                } catch (e) { pushAgentLog(vm, 'error', 'Context review error: ' + (e.message || e)); }
                                                                break;
                                                            case 'ask-question':
                                                                try {
                                                                    if (parsed && parsed.id && parsed.question) {
                                                                        vm.pendingQuestion = parsed; vm.questionAnswers = {}; vm.questionError = ''; vm.showQuestionModal = true;
                                                                        pushAgentLog(vm, 'info', '❓ Question from agent: ' + parsed.question);
                                                                        if (vm.questionTimeout) $timeout.cancel(vm.questionTimeout);
                                                                        vm.questionTimeout = $timeout(function () { if (vm.showQuestionModal) vm.cancelQuestion(); }, 55000);
                                                                    }
                                                                } catch (e) { pushAgentLog(vm, 'error', 'Question error: ' + (e.message || e)); }
                                                                break;
                                                            case 'cohesion':
                                                                if (parsed && parsed.issues && parsed.issues.length) {
                                                                    vm.cohesionIssues = parsed.issues; vm.cohesionFile = parsed.file || '';
                                                                    pushAgentLog(vm, 'info', '🔍 Cohesion: ' + parsed.issues.length + ' issue(s) found' + (vm.cohesionFile ? ' in ' + vm.cohesionFile : ''));
                                                                    angular.forEach(parsed.issues, function (issue) { pushAgentLog(vm, 'info', '  ⚠ ' + issue); });
                                                                    var activeCardC = vm.findCardById(vm.activeCardId);
                                                                    if (activeCardC) { activeCardC._cohesion = { file: vm.cohesionFile, issues: angular.copy(vm.cohesionIssues) }; vm.saveCards(); }
                                                                } else { pushAgentLog(vm, 'info', '🔍 Cohesion: no issues found'); }
                                                                break;
                                                            case 'done':
                                                                vm.sendSystemToast(); vm.streamingActive = false; vm.resumeTerminalPolling(); vm.steeringContext = '';
                                                                var elapsed = vm._agentStartTime ? Date.now() - vm._agentStartTime : 0;
                                                                var elapsedStr = elapsed > 0 ? (elapsed >= 60000 ? Math.floor(elapsed / 60000) + 'm ' + (elapsed % 60000) / 1000 + 's' : Math.floor(elapsed / 1000) + 's') : '';

                                                                var editsApplied = parsed && parsed.editsApplied;
                                                                if (!editsApplied && !vm.activeCardId) vm.stopAgent();
                                                                var incomplete = parsed && parsed.incomplete;
                                                                if (parsed && parsed.warning) vm.aiResponse = parsed.warning;

                                                                pushAgentLog(vm, editsApplied ? 'info' : 'warn', editsApplied ? 'Agent finished' : 'Agent finished without file edits', { filesEdited: (parsed && parsed.filesEdited) ? parsed.filesEdited.length : 0, warning: parsed && parsed.warning, duration: elapsedStr || undefined });
                                                                pushAgentLog(vm, 'info', '⏱ ' + (elapsedStr || elapsed + 'ms'));

                                                                var finalThinking = (parsed && parsed.thinking) || vm.streamingThinking;
                                                                var finalSummary = (parsed && parsed.summary) || vm.streamingSummary;
                                                                var finalSteps = (parsed && parsed.steps) ? parsed.steps.map(normalizeStep) : angular.copy(vm.streamingSteps);

                                                                if (parsed && parsed.filesEdited && parsed.filesEdited.length) vm.streamingFilesEdited = parsed.filesEdited;
                                                                else refreshFilesEditedFromSteps(vm);

                                                                vm.agentResult = { summary: finalSummary, thinking: finalThinking, filesEdited: vm.streamingFilesEdited, steps: finalSteps, planItems: angular.copy(vm.planItems), warning: parsed && parsed.warning, incomplete: incomplete, needsClarification: parsed && parsed.needsClarification, question: parsed && (parsed.question || parsed.warning || finalSummary) };
                                                                vm.aiResponse = (parsed && parsed.warning) || finalSummary || 'Agent completed.';

                                                                var analysis = { summary: finalSummary, thinking: finalThinking, steps: finalSteps, filesEdited: vm.streamingFilesEdited, planItems: angular.copy(vm.planItems), warning: parsed && parsed.warning, incomplete: incomplete, needsClarification: parsed && parsed.needsClarification, question: parsed && (parsed.question || parsed.warning || finalSummary) };
                                                                var doIdx = vm.state.doing.findIndex(function (c) { return c.id === card.id; });
                                                                if (doIdx !== -1) { vm.state.doing[doIdx].agentAnalysis = analysis; vm.state.doing[doIdx].agentLog = angular.copy(vm.agentActivityLog); }

                                                                if (vm._agentStopped || card.id !== vm.activeCardId) { $scope.$applyAsync(); return; }

                                                                if (vm.planItems && vm.planItems.length) {
                                                                    var allDone = vm.planItems.every(function (pi) { return pi.done; });
                                                                    if (!allDone) { incomplete = true; pushAgentLog(vm, 'warn', 'Plan has ' + vm.planItems.filter(function (pi) { return !pi.done; }).length + ' unchecked step(s) — card stays in Doing'); }
                                                                    else incomplete = false;
                                                                }

                                                                function recordBenchmarkScore() {
                                                                    if (!card._benchmark) return;
                                                                    vm.benchmarkRunning = false; vm.benchmarkLevel = null;
                                                                    var completed = 0, total = card._benchmarkTotalSteps || 0;
                                                                    if (vm.planItems && vm.planItems.length) { completed = vm.planItems.filter(function (pi) { return pi.done; }).length; if (total === 0) total = vm.planItems.length; }
                                                                    if (total === 0) total = 1;
                                                                    var bmElapsed = vm._agentStartTime ? Date.now() - vm._agentStartTime : 0;
                                                                    $http.post('/api/benchmark/save-score', { level: card._benchmarkLevel || 1, stepsCompleted: completed, totalSteps: total, scorePercent: Math.round((completed / total) * 1000) / 10, status: completed === total ? 'completed' : completed > 0 ? 'partial' : 'failed', modelUsed: (vm.systemInfoCustom && vm.systemInfoCustom.model) || '', durationMs: bmElapsed, errorReason: vm.agentResult && (vm.agentResult.error || vm.agentResult.warning) || '' });
                                                                    var bIdx = vm.state.todo.indexOf(card); if (bIdx < 0) bIdx = vm.state.doing.indexOf(card); if (bIdx < 0) bIdx = vm.state.done.indexOf(card);
                                                                    if (bIdx >= 0) { var col = vm.state.todo.indexOf(card) >= 0 ? 'todo' : vm.state.doing.indexOf(card) >= 0 ? 'doing' : 'done'; vm.state[col].splice(bIdx, 1); vm.saveCards(); }
                                                                }

                                                                function finishCard() {
                                                                    if (card._benchmark && !incomplete) { recordBenchmarkScore(); return; }
                                                                    if (!incomplete) { pushAgentLog(vm, 'log', `Plan completed — moving card to ${card.selfImproving ? 'Self-Improving' : 'Done'} column.`); vm.moveCardToDone(card); }
                                                                    if (incomplete && card.id === vm.activeCardId) {
                                                                        card._agentIteration = (card._agentIteration || 0) + 1; var MAX_ITERATIONS = 5;
                                                                        if (card._agentIteration >= MAX_ITERATIONS) { pushAgentLog(vm, 'warn', 'Max iterations reached — stopping'); incomplete = false; if (card._benchmark) { recordBenchmarkScore(); return; } }
                                                                        else { pushAgentLog(vm, 'info', 'Re-starting agent (' + card._agentIteration + '/' + MAX_ITERATIONS + ') — ' + (vm.planItems ? vm.planItems.filter(function (pi) { return !pi.done; }).length : 'quality') + ' issue(s) remain'); $timeout(function () { vm.executeAgent(card, true); }, 1000); return; }
                                                                    }
                                                                    if (vm.autoQueue) {
                                                                        $timeout(function () {
                                                                            var readyTodo = vm.state.todo.filter(function (c) { return c.filePath === vm.selectedProject && c.ready && !c.selfImproving; });
                                                                            if (readyTodo.length) { var next = readyTodo[readyTodo.length - 1]; vm.moveCardToDoing(next.id); vm.executeAgent(next); return; }
                                                                            var siCards = vm.state.selfImproving.filter(function (c) { return c.filePath === vm.selectedProject && c.selfImproving; });
                                                                            if (siCards.length) { var nextSi = siCards[siCards.length - 1]; nextSi.ready = true; vm.moveCardToDoing(nextSi.id); vm.executeAgent(nextSi); }
                                                                        }, 500);
                                                                    }
                                                                }

                                                                if (!incomplete && card.autoPr && card.prStatus && card.prStatus.branch) {
                                                                    card.prStatus.status = 'creating-pr'; pushAgentLog(vm, 'info', 'Creating PR for branch ' + card.prStatus.branch + '...');
                                                                    $http.post('/api/pr/finish', { projectPath: proj, cardId: card.id, cardText: card.text, branchName: card.prStatus.branch, summary: finalSummary, originalBranch: card.prStatus.originalBranch }).then(function (prResp) {
                                                                        if (prResp.data && prResp.data.success) { card.prStatus = { status: 'pr-created', branch: card.prStatus.branch, prUrl: prResp.data.prUrl }; pushAgentLog(vm, 'info', 'PR created: ' + (prResp.data.prUrl || 'Check your repository')); }
                                                                        else { card.prStatus = { status: 'error', error: (prResp.data && prResp.data.error) || 'PR creation failed', branch: card.prStatus.branch }; pushAgentLog(vm, 'warn', 'PR creation: ' + card.prStatus.error); }
                                                                        finishCard();
                                                                    }, function (err) { card.prStatus = { status: 'error', error: err.statusText || 'PR failed', branch: card.prStatus.branch }; pushAgentLog(vm, 'warn', 'PR creation failed: ' + card.prStatus.error); finishCard(); });
                                                                } else { if (incomplete) pushAgentLog(vm, 'warn', 'Card kept in Doing — no files were modified'); finishCard(); }
                                                                break;
                                                            case 'error':
                                                                vm.streamingActive = false; vm.resumeTerminalPolling(); pushAgentLog(vm, 'error', parsed ? parsed.message : data); vm.agentResult = { error: parsed ? parsed.message : data }; vm.activeCardId = null; vm.activeCardIds = new Set();
                                                                if (card._benchmark) { $http.post('/api/benchmark/save-score', { level: card._benchmarkLevel || 1, stepsCompleted: 0, totalSteps: card._benchmarkTotalSteps || 1, scorePercent: 0, status: 'error', modelUsed: (vm.systemInfoCustom && vm.systemInfoCustom.model) || '', durationMs: vm._agentStartTime ? Date.now() - vm._agentStartTime : 0, errorReason: parsed ? parsed.message : data }); var errIdx = vm.state.doing.indexOf(card); if (errIdx >= 0) { vm.state.doing.splice(errIdx, 1); vm.saveCards(); } }
                                                                break;
                                                        }
                                                    }
                                                }
                                            });
                                            try { $scope.$applyAsync(); } catch (e) { }
                                            readNext();
                                        }).catch(function (readErr) {
                                            if (readErr && readErr.name === 'AbortError') return;
                                            vm.streamingActive = false; vm.resumeTerminalPolling(); vm.agentResult = { error: 'Stream read error: ' + (readErr && readErr.message || readErr) }; $scope.$applyAsync();
                                        });
                                    }
                                    readNext();
                                }).catch(function (err) { vm.streamingActive = false; vm.resumeTerminalPolling(); });
                        }

                        if (card.autoPr && proj) {
                            pushAgentLog(vm, 'info', 'Creating PR branch...');
                            $http.post('/api/pr/start', { projectPath: proj, cardId: card.id, cardText: card.text }).then(function (resp) {
                                if (resp.data && resp.data.success) { card.prStatus = { status: 'branch-created', branch: resp.data.branchName, originalBranch: resp.data.originalBranch }; pushAgentLog(vm, 'info', 'PR branch: ' + card.prStatus.branch); }
                                else { card.prStatus = { status: 'error', error: 'Branch creation failed' }; pushAgentLog(vm, 'warn', 'PR branch failed'); }
                                startAgent();
                            }, function () { startAgent(); });
                        } else { startAgent(); }
                    } catch (e) { console.log("executeAgent error", e); }
                };

                vm.stopAgent = function (card) {
                    vm._agentStopped = true; if (vm.abortController) vm.abortController.abort();
                    vm.abortController = new AbortController(); vm.streamingActive = false; vm.agentResult = { warning: 'Agent stopped by user.' };
                    pushAgentLog(vm, 'warn', 'Agent stopped by user'); vm.activeCardId = null; vm.activeCardIds = new Set(); vm.resumeTerminalPolling();
                };

                vm.askAI = function () {
                    if (!vm.aiPrompt) return $window.alert('Enter a prompt');
                    vm.aiResponse = 'Thinking...';
                    $http.post('/api/ai/generate', { prompt: vm.aiPrompt }).then(function (resp) { vm.aiResponse = typeof resp.data === 'string' ? resp.data : JSON.stringify(resp.data, null, 2); }, function (err) { vm.aiResponse = 'Error: ' + (err.statusText || err); });
                };

                vm.sendAiChat = function () {
                    if (!vm.aiChatInput || vm.aiChatLoading) return;
                    var userMsg = vm.aiChatInput; vm.aiChatInput = ''; vm.aiChatMessages.push({ role: 'user', content: userMsg });
                    if (vm.chatMode === 'build') {
                        var tempCard = { id: 'chat-' + Date.now(), text: userMsg, filePath: vm.selectedProject, attached: [], ready: true, selfImproving: false, isDecomposing: false };
                        vm.aiChatMessages.push({ role: 'assistant', content: '🤖 Starting agent pipeline...', _progress: true });
                        vm.executeAgent(tempCard);
                        var unwatch = $scope.$watch(function () { return vm.agentResult; }, function (newVal) { if (newVal) { var lastMsg = vm.aiChatMessages[vm.aiChatMessages.length - 1]; if (lastMsg && lastMsg._progress) { lastMsg.content = newVal.error ? '❌ ' + newVal.error : '✅ ' + (newVal.summary || 'Agent completed'); delete lastMsg._progress; } unwatch(); } });
                        return;
                    }
                    vm.aiChatLoading = true;
                    var messages = vm.aiChatMessages.map(function (m) { return { role: m.role, content: m.content }; });
                    $http.post('/api/ai/generate', { messages: messages }).then(function (resp) {
                        var content = ''; if (resp.data && resp.data.choices && resp.data.choices[0]) content = resp.data.choices[0].message.content; else if (typeof resp.data === 'string') content = resp.data;
                        vm.aiChatMessages.push({ role: 'assistant', content: content }); vm.aiChatLoading = false;
                    }, function (err) { vm.aiChatMessages.push({ role: 'assistant', content: 'Error: ' + (err.statusText || err) }); vm.aiChatLoading = false; });
                };

                vm.clearAiChat = function () { vm.aiChatMessages = []; vm.aiChatInput = ''; };
                vm.submitClarification = function () {
                    var reply = (vm.clarificationReply || '').trim(); if (!reply) return;
                    var card = vm.findCardById ? vm.findCardById(vm.activeCardId) : null;
                    if (card) { card.text = (card.text || '') + '\n\nClarification requested: ' + (vm.agentResult && vm.agentResult.question || 'Clarification') + '\nUser answer: ' + reply; delete card.agentAnalysis; delete card.agentLog; vm.saveCards(); vm.agentResult = null; vm.executeAgent(card); }
                    vm.clarificationReply = '';
                };

                vm.dismissContextReview = function () { if (vm.contextReviewTimer) { $interval.cancel(vm.contextReviewTimer); vm.contextReviewTimer = null; } vm.pendingContextReview = null; };
                vm.confirmContextReview = function () {
                    if (!vm.pendingContextReview) return;
                    var selected = []; vm.pendingContextReview.files.forEach(function (f) { if (f.keep !== false) selected.push(f.path); });
                    $http.post('/api/agent/context-review/confirm', { id: vm.pendingContextReview.id, files: selected }).then(function () {
                        var card = vm.findCardById ? vm.findCardById(vm.activeCardId) : null;
                        if (card && selected.length > 0) { card.confirmedContextFiles = selected; var existing = Array.isArray(card.attached) ? card.attached : []; selected.forEach(function (f) { if (existing.indexOf(f) === -1) existing.push(f); }); card.attached = existing; vm.saveCards(); }
                        vm.pendingContextReview = null;
                    });
                };

                vm.submitQuestion = function () {
                    if (!vm.pendingQuestion) return;
                    if (vm.questionTimeout) { $timeout.cancel(vm.questionTimeout); vm.questionTimeout = null; }
                    var answers = {}; vm.pendingQuestion.fields.forEach(function (f) { answers[f.key] = (vm.questionAnswers[f.key] || '').trim(); });
                    $http.post('/api/agent/questions/answer', { id: vm.pendingQuestion.id, answers: answers }).then(function () { vm.showQuestionModal = false; vm.pendingQuestion = null; }, function (err) { vm.questionError = 'Failed to submit: ' + (err.data || err.statusText || err); });
                };
                vm.cancelQuestion = function () {
                    if (!vm.pendingQuestion) return;
                    if (vm.questionTimeout) { $timeout.cancel(vm.questionTimeout); vm.questionTimeout = null; }
                    $http.post('/api/agent/questions/answer', { id: vm.pendingQuestion.id, answers: {} }).then(function () { vm.showQuestionModal = false; vm.pendingQuestion = null; });
                };

                vm.openBenchmarksPanel = function () { vm.showBenchmarksPanel = true; $http.get('/api/benchmark/scores').then(function (resp) { vm.benchmarkScores = resp.data || []; }); $http.get('/api/benchmark/system-info').then(function (resp) { vm.systemInfoCustom = resp.data.custom || {}; }); };
                vm.closeBenchmarksPanel = function () { vm.showBenchmarksPanel = false; };
                vm.startBenchmark = function (level) {
                    if (vm.benchmarkRunning || vm.streamingActive) return; vm.benchmarkRunning = true; vm.benchmarkLevel = level;
                    $http.get('/api/benchmark/plans').then(function (resp) {
                        var plan = (resp.data || []).find(function (p) { return p.level === level; });
                        if (!plan) return vm.benchmarkRunning = false;
                        var card = { id: 'benchmark_' + level + '_' + Date.now(), text: plan.description, filePath: vm.selectedProject, priority: 'high', _benchmark: true, _benchmarkLevel: level, _benchmarkTotalSteps: plan.steps.length, ready: true };
                        vm.state.todo.push(card); vm.saveCards(); vm.executeAgent(card); vm.closeBenchmarksPanel();
                    });
                };

                vm.formatBenchmarkDuration = function (durMs) {
                    if (durMs === null || durMs === undefined) return '';
                    var seconds = Math.floor(durMs / 1000); var minutes = Math.floor(seconds / 60); seconds = seconds % 60; var hours = Math.floor(minutes / 60); minutes = minutes % 60;
                    return (hours > 0 ? hours + 'h ' : '') + (minutes > 0 ? minutes + 'm ' : '') + seconds + 's';
                }

                vm.sendBenchmarkToServer = function (s) {
                    var benchmarkDto = {
                        Token: vm.bughostedClientId,
                        Date: s.date,
                        Benchmark: s.level,
                        Steps: s.stepsCompleted + "/" + s.totalSteps,
                        Score: s.scorePercent || '0',
                        Status: s.status || '',
                        Duration: s.durationMs.toString() || '0',
                        Model: s.modelUsed || '',
                        OS: vm.systemInfoCustom.os || vm.systemInfoDetected.os || '',
                        CPU: vm.systemInfoCustom.cpu || vm.systemInfoDetected.cpu || '',
                        RAM: vm.systemInfoCustom.ramGb || vm.systemInfoDetected.ramBytes || null,
                        GPU: vm.systemInfoCustom.gpu || vm.systemInfoDetected.gpu || ''
                    };

                    $http.post('/bughostedcontroller/addbenchmark', benchmarkDto)
                        .then(function (response) {
                            console.log('Successfully sent benchmark to server:', response.data);
                            alert('Benchmark successfully sent to BugHosted!');
                        })
                        .catch(function (error) {
                            console.error('Error sending benchmark to server:', error);
                            if (error && error.message) {
                                alert('Failed to send benchmark. Error details:\n' + error.message);
                            } else {
                                alert('Failed to send benchmark due to an unknown error.');
                            }
                        });
                };
                vm.saveSystemInfo = function () { $http.post('/api/benchmark/system-info', vm.systemInfoCustom).then(function () { vm.systemInfoSaved = true; $timeout(function () { vm.systemInfoSaved = false; }, 2000); }); };
                vm.resetSystemInfo = function () { vm.systemInfoCustom = { os: '', cpu: '', ramGb: null, gpu: '', model: '', benchmarkProjectRoot: '' }; vm.saveSystemInfo(); };
                vm.deleteBenchmarkScore = function (score) { $http.delete('/api/benchmark/scores/' + encodeURIComponent(score.id)).then(function () { var idx = vm.benchmarkScores.indexOf(score); if (idx >= 0) vm.benchmarkScores.splice(idx, 1); }); };
            }
        };
    }]);