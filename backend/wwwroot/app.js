angular.module('kanbanApp', [])
  .controller('MainCtrl', ['$http', '$interval', '$window', function($http, $interval, $window) {
    const vm = this;
    const STORAGE_KEY = 'kanban.cards';

    vm.aiPrompt = '';
    vm.aiResponse = '';
    vm.termInput = '';
    vm.terminalOutput = '';

    function uid() { return Math.random().toString(36).slice(2,9); }

    function loadCards(){
      const raw = $window.localStorage.getItem(STORAGE_KEY);
      return raw ? JSON.parse(raw) : { todo:[], doing:[], done:[] };
    }

    function saveCards(){ $window.localStorage.setItem(STORAGE_KEY, JSON.stringify(vm.state)); }

    vm.state = loadCards();

    vm.addCard = function(col){
      const text = $window.prompt('Card text'); if(!text) return;
      vm.state[col].push({ id: uid(), text }); saveCards();
    };

    vm.moveCard = function(id, from, to){
      const idx = vm.state[from].findIndex(c=>c.id===id); if(idx===-1) return;
      const [card] = vm.state[from].splice(idx,1); vm.state[to].push(card); saveCards();
    };

    vm.selectCard = function(card){ vm.aiPrompt = card.text; };

    vm.askAI = function(){
      if(!vm.aiPrompt) return $window.alert('Enter a prompt');
      vm.aiResponse = 'Thinking...';
      $http.post('/api/ai/generate', { prompt: vm.aiPrompt })
        .then(function(resp){
          if(typeof resp.data === 'string') vm.aiResponse = resp.data;
          else vm.aiResponse = JSON.stringify(resp.data, null, 2);
        }, function(err){ vm.aiResponse = 'Error: ' + (err.statusText || err); });
    };

    vm.startTerminal = function(){ $http.post('/api/terminal/start').catch(()=>{}); };

    vm.sendCmd = function(){ if(!vm.termInput) return; $http.post('/api/terminal/exec', { command: vm.termInput }).then(function(){ vm.termInput = ''; vm.refreshTerminal(); }); };

    vm.refreshTerminal = function(){
      $http.get('/api/terminal/output').then(function(resp){ vm.terminalOutput = resp.data.output || ''; });
    };

    // initial load + polling
    vm.refreshTerminal();
    $interval(vm.refreshTerminal, 2000);
  }]);
