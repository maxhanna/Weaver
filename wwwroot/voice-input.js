'use strict';

angular.module('kanbanApp').factory('VoiceInput', function ($window, $timeout) {
  var recognition = null;
  var active = false;
  var stopFlag = false;
  var currentCard = null;
  var timer = 0;
  var timerHandle = null;
  var baseText = '';
  var lastProcessedIndex = -1;

  var SpeechRecognition = $window.SpeechRecognition || $window.webkitSpeechRecognition;

  function cleanup() {
    stopFlag = true;
    if (recognition) {
      try { recognition.abort(); } catch (_) {}
      recognition = null;
    }
    active = false;
    currentCard = null;
    if (timerHandle) {
      $timeout.cancel(timerHandle);
      timerHandle = null;
    }
    timer = 0;
  }

  function startTimer(scope) {
    timer = 0;
    function tick() {
      timer++;
      timerHandle = $timeout(tick, 1000);
    }
    tick();
  }

  return {
    isSupported: function () {
      return !!SpeechRecognition;
    },

    isActive: function () {
      return active;
    },

    getSeconds: function () {
      return timer;
    },

    start: function (card, scope) {
      if (!SpeechRecognition || active) return;

      active = true;
      stopFlag = false;
      currentCard = card;
      baseText = card.text || '';
      lastProcessedIndex = -1;

      recognition = new SpeechRecognition();
      recognition.continuous = true;
      recognition.interimResults = false;

      recognition.onresult = function (e) {
        if (stopFlag || !currentCard) return;
        for (var i = e.resultIndex; i < e.results.length; i++) {
          if (i <= lastProcessedIndex) continue;
          if (e.results[i].isFinal) {
            var text = e.results[i][0].transcript;
            currentCard.text = baseText + (baseText && text ? ' ' : '') + text;
            lastProcessedIndex = i;
          }
        }
        scope.$applyAsync();
      };

      recognition.onerror = function () {
        active = false;
        scope.$applyAsync();
      };

      recognition.onend = function () {
        if (!stopFlag) {
          try { recognition.start(); } catch (_) {}
        }
      };

      startTimer(scope);
      recognition.start();
    },

    stop: function () {
      stopFlag = true;
      if (recognition) {
        try { recognition.stop(); } catch (_) {}
      }
      if (timerHandle) {
        $timeout.cancel(timerHandle);
        timerHandle = null;
      }
      active = false;
      timer = 0;
      currentCard = null;
    },

    reset: cleanup
  };
});
