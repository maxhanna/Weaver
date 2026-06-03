'use strict';

angular.module('kanbanApp').factory('CalendarMixin', function ($http, $window, $timeout) {
  function uid() { return Math.random().toString(36).slice(2, 9); }

  return {
    init: function (vm, $scope) {
      vm.calCards = [];
      vm.calDays = [];
      vm.calYear = new Date().getFullYear();
      vm.calMonth = new Date().getMonth();
      vm.calWeekdays = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
      vm.calEditCardData = null;

      vm.calMonthName = (function () {
        var m = new Date(vm.calYear, vm.calMonth, 1);
        return m.toLocaleString('default', { month: 'long' });
      })();

      function localDateStr(date) {
        var y = date.getFullYear();
        var m = String(date.getMonth() + 1).padStart(2, '0');
        var d = String(date.getDate()).padStart(2, '0');
        return y + '-' + m + '-' + d;
      }

      function scheduleUpdate() {
        try { if (!$scope.$$phase) $scope.$applyAsync(); } catch (e) {}
      }

      function normalizeCalCard(c) {
        if (typeof c.date !== 'string' || c.date.length > 10) {
          if (c.date && typeof c.date === 'object' && typeof c.date.getFullYear === 'function') {
            c.date = localDateStr(c.date);
          } else {
            c.date = c.date ? localDateStr(new Date(c.date)) : '';
          }
        }
        if (typeof c.time !== 'string' || c.time.length > 5) {
          if (c.time && typeof c.time === 'object' && typeof c.time.getHours === 'function') {
            c.time = pad2(c.time.getHours()) + ':' + pad2(c.time.getMinutes());
          } else if (c.time && String(c.time).length > 10) {
            var t = new Date(c.time);
            c.time = pad2(t.getHours()) + ':' + pad2(t.getMinutes());
          } else {
            c.time = '';
          }
        }
        return c;
      }
      function pad2(n) { return String(n).padStart(2, '0'); }

      vm.projectName = function (path) {
        if (!path) return '';
        if (vm.projects) {
          for (var pi = 0; pi < vm.projects.length; pi++) {
            var p = vm.projects[pi];
            if ((p.Path || p.path) === path) return p.Name || p.name || path;
          }
        }
        return path.split(/[\/\\]/).pop() || path;
      };

      vm.loadCalendarCards = function () {
        $http.get('/api/calendar/load').then(function (resp) {
          try {
            var data = resp.data;
            if (typeof data === 'string') data = JSON.parse(data);
            if (Array.isArray(data)) {
              for (var ci = 0; ci < data.length; ci++) normalizeCalCard(data[ci]);
              vm.calCards = data;
            }
          } catch (e) {
            console.warn('Failed to parse calendar data');
          }
          vm.calBuildDays();
        }, function () {
          vm.calBuildDays();
        });
      };

      vm.saveCalendarCards = function () {
        $http.post('/api/calendar/save', vm.calCards).catch(function (err) {
          console.error('Failed to save calendar data:', err);
        });
      };

      vm.calBuildDays = function () {
        var year = vm.calYear;
        var month = vm.calMonth;
        var first = new Date(year, month, 1);
        var last = new Date(year, month + 1, 0);
        var startPad = first.getDay();
        var daysInMonth = last.getDate();
        var today = new Date();
        var todayStr = localDateStr(today);
        var days = [];
        var cards = vm.calCards;
        var project = vm.selectedProject;

        function cardsForDate(dateStr) {
          var result = [];
          for (var ci = 0; ci < cards.length; ci++) {
            var c = cards[ci];
            if (c.date === dateStr && (!project || c.project === project)) {
              result.push(c);
            }
          }
          return result;
        }

        function isWeekend(d) {
          var day = d.getDay();
          return day === 0 || day === 6;
        }

        var prevMonthLast = new Date(year, month, 0).getDate();
        for (var p = startPad - 1; p >= 0; p--) {
          var d = prevMonthLast - p;
          var dt = new Date(year, month - 1, d);
          var ds = localDateStr(dt);
          days.push({ num: d, date: ds, inMonth: false, isToday: ds === todayStr, isWeekend: isWeekend(dt), cards: cardsForDate(ds) });
        }

        for (var i = 1; i <= daysInMonth; i++) {
          var dt2 = new Date(year, month, i);
          var ds2 = localDateStr(dt2);
          days.push({ num: i, date: ds2, inMonth: true, isToday: ds2 === todayStr, isWeekend: isWeekend(dt2), cards: cardsForDate(ds2) });
        }

        var remaining = 7 - (days.length % 7);
        if (remaining < 7) {
          for (var j = 1; j <= remaining; j++) {
            var dt3 = new Date(year, month + 1, j);
            var ds3 = localDateStr(dt3);
            days.push({ num: j, date: ds3, inMonth: false, isToday: ds3 === todayStr, isWeekend: isWeekend(dt3), cards: cardsForDate(ds3) });
          }
        }

        vm.calDays = days;
        vm.calMonthName = first.toLocaleString('default', { month: 'long' });
        scheduleUpdate();
      };

      vm.calPrevMonth = function () {
        vm.calMonth--;
        if (vm.calMonth < 0) { vm.calMonth = 11; vm.calYear--; }
        vm.calBuildDays();
      };

      vm.calNextMonth = function () {
        vm.calMonth++;
        if (vm.calMonth > 11) { vm.calMonth = 0; vm.calYear++; }
        vm.calBuildDays();
      };

      vm.calToday = function () {
        var now = new Date();
        vm.calYear = now.getFullYear();
        vm.calMonth = now.getMonth();
        vm.calBuildDays();
      };

      vm.calAddCard = function () {
        var now = new Date();
        vm.calEditCardData = {
          id: null,
          date: localDateStr(now),
          time: '',
          text: '',
          priority: 'medium',
          cronExpression: '',
          project: vm.selectedProject || ''
        };
        scheduleUpdate();
      };

      vm.calEditCard = function (card, $event) {
        if ($event && $event.target.classList.contains('cal-card-del')) return;
        try {
          vm.calEditCardData = JSON.parse(JSON.stringify(card));
        } catch (e) {
          vm.calEditCardData = angular.copy(card);
        }
        scheduleUpdate();
      };

      vm.calCloseEdit = function () {
        vm.calEditCardData = null;
        scheduleUpdate();
      };

      vm.calSaveCard = function () {
        try {
          var data = vm.calEditCardData;
          if (!data || !data.text || !data.date) return;

          var saved = normalizeCalCard(JSON.parse(JSON.stringify(data)));
          if (saved.id) {
            var idx = -1;
            for (var ci = 0; ci < vm.calCards.length; ci++) {
              if (vm.calCards[ci].id === saved.id) { idx = ci; break; }
            }
            if (idx !== -1) {
              vm.calCards[idx] = saved;
            }
          } else {
            saved.id = uid();
            saved.createdAt = new Date().toISOString();
            vm.calCards.push(saved);
          }
          vm.calEditCardData = null;
          vm.saveCalendarCards();
          vm.calBuildDays();
        } catch (e) {
          console.error('Error saving calendar card:', e);
        }
        scheduleUpdate();
      };

      vm.calDeleteCard = function (card, $event) {
        try {
          if ($event) $event.stopPropagation();
          if (!$window.confirm('Delete this calendar card?')) return;
          var id = card.id || (vm.calEditCardData && vm.calEditCardData.id);
          if (!id) return;
          var filtered = [];
          for (var ci = 0; ci < vm.calCards.length; ci++) {
            if (vm.calCards[ci].id !== id) filtered.push(vm.calCards[ci]);
          }
          vm.calCards = filtered;
          vm.calEditCardData = null;
          vm.saveCalendarCards();
          vm.calBuildDays();
        } catch (e) {
          console.error('Error deleting calendar card:', e);
        }
        scheduleUpdate();
      };

      vm.loadCalendarCards();
    }
  };
});
