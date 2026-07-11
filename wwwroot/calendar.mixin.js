// calendar.mixin.js
angular.module('kanbanApp')
    .factory('CalendarMixin', ['$http', '$interval', function ($http, $interval) {
        var _calTimer = null;
        function uid() { return Math.random().toString(36).slice(2, 9); }

        return {
            init: function (vm, $scope) {
                vm.calCards = [];

                function cronMatches(expr, date) {
                    try {
                        var parts = expr.trim().split(/\s+/); if (parts.length !== 5) return false;
                        function matchField(field, val) {
                            if (field === '*') return true;
                            if (field.indexOf('*/') === 0) { var interval = parseInt(field.slice(2), 10); return interval > 0 && val % interval === 0; }
                            var vals = field.split(','); for (var i = 0; i < vals.length; i++) { if (parseInt(vals[i], 10) === val) return true; }
                            return false;
                        }
                        return matchField(parts[0], date.getMinutes()) && matchField(parts[1], date.getHours()) && matchField(parts[2], date.getDate()) && matchField(parts[3], date.getMonth() + 1) && matchField(parts[4], date.getDay());
                    } catch (e) { return false; }
                }

                function processCalendarEvents() {
                    $http.get('/api/calendar/load').then(function (resp) {
                        try {
                            var data = resp.data; if (typeof data === 'string') data = JSON.parse(data); if (!Array.isArray(data)) return;
                            var now = new Date(); var todayStr = now.getFullYear() + '-' + String(now.getMonth() + 1).padStart(2, '0') + '-' + String(now.getDate()).padStart(2, '0');
                            var currentMinutes = now.getHours() * 60 + now.getMinutes(); var changed = false;

                            for (var ci = 0; ci < data.length; ci++) {
                                var cal = data[ci]; if (!cal.date || !cal.text) continue; var shouldFire = false;
                                if (cal.cronExpression) {
                                    if (cronMatches(cal.cronExpression, now)) { var lastFired = cal.lastFired ? new Date(cal.lastFired).getTime() : 0; if (now.getTime() - lastFired > 60000) shouldFire = true; }
                                } else {
                                    if (cal.processed) continue; var calMinute = 0; if (cal.time) { var tp = cal.time.split(':'); calMinute = parseInt(tp[0], 10) * 60 + parseInt(tp[1], 10); }
                                    if (cal.date < todayStr || (cal.date === todayStr && calMinute <= currentMinutes)) shouldFire = true;
                                }

                                if (shouldFire) {
                                    var newCard = { id: uid(), text: cal.text, filePath: cal.project || vm.selectedProject, createdAt: now.toISOString(), priority: cal.priority || 'medium', ready: true, attached: [], selfImproving: false, isDecomposing: false };
                                    vm.state.todo.push(newCard); vm.saveCards(); changed = true;
                                    if (cal.cronExpression) cal.lastFired = now.toISOString(); else cal.processed = true;
                                    if (!vm.streamingActive) vm.executeAgent(newCard);
                                }
                            }
                            if (changed) $http.post('/api/calendar/save', data).catch(function () { });
                        } catch (e) { console.log("processCalendarEvents error ", e); }
                    }, function () { });
                }

                vm.startCalendarProcessing = function () { stopCalendarProcessing(); _calTimer = $interval(processCalendarEvents, 60000, 0, false); processCalendarEvents(); };
                function stopCalendarProcessing() { if (_calTimer) { $interval.cancel(_calTimer); _calTimer = null; } }

                $scope.$on('$destroy', stopCalendarProcessing);
            }
        };
    }]);