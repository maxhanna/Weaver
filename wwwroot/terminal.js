// terminal.mixin.js
angular.module('kanbanApp')
    .factory('TerminalMixin', ['$http', '$interval', '$timeout', function ($http, $interval, $timeout) {
        var _terminalInterval = null, _approvalInterval = null;

        return {
            init: function (vm, $scope) {
                vm.terminalOutput = '';
                vm.termInput = '';
                vm.pendingTerminalApprovals = [];

                vm.startTerminal = function () { $http.post('/api/terminal/start').catch(function () { }); };
                vm.sendCmd = function () {
                    if (!vm.termInput) return;
                    $http.post('/api/terminal/exec', { command: vm.termInput }).then(function () { vm.termInput = ''; vm.refreshTerminal(); });
                };

                vm.refreshTerminal = function () {
                    if (vm.destroyed) return;
                    fetch('/api/terminal/output').then(function (r) { return r.json(); }).then(function (data) {
                        var newOutput = (data && data.output) || '';
                        if (newOutput !== vm.terminalOutput) {
                            vm.terminalOutput = newOutput;
                            if (!$scope.$$phase) $scope.$digest();
                            $timeout(function () { var el = document.querySelector('.terminalOutput'); if (el) el.scrollTop = el.scrollHeight; }, 0, false);
                        }
                    });
                };

                vm.refreshTerminalApprovals = function () {
                    if (vm.destroyed) return;
                    fetch('/api/terminal/approvals/pending').then(function (r) { return r.json(); }).then(function (data) {
                        vm.pendingTerminalApprovals = (data && data.approvals) || [];
                    }, function () { vm.pendingTerminalApprovals = []; });
                };

                vm.approveTerminalCommand = function (approval, scope) {
                    if (!approval) return;
                    $http.post('/api/terminal/approvals/approve', { id: approval.id || approval.Id, scope: scope || 'once' }).then(function () { vm.refreshTerminalApprovals(); vm.loadConfig(); });
                };

                vm.rejectTerminalCommand = function (approval) {
                    if (!approval) return;
                    $http.post('/api/terminal/approvals/reject', { id: approval.id || approval.Id }).then(function () { vm.refreshTerminalApprovals(); });
                };

                vm.pauseTerminalPolling = function () {
                    if (_terminalInterval) { $interval.cancel(_terminalInterval); _terminalInterval = null; }
                    if (_approvalInterval) { $interval.cancel(_approvalInterval); _approvalInterval = null; }
                };

                vm.resumeTerminalPolling = function () {
                    if (!_terminalInterval) _terminalInterval = $interval(vm.refreshTerminal, 3000, 0, false);
                    if (!_approvalInterval) _approvalInterval = $interval(vm.refreshTerminalApprovals, 1500, 0, false);
                };

                vm.refreshTerminal();
                vm.resumeTerminalPolling();

                $scope.$on('$destroy', function () { vm.pauseTerminalPolling(); });
            }
        };
    }]);