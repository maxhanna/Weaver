angular.module('kanbanApp')
    .factory('FilePickerMixin', ['$http', '$timeout', function ($http, $timeout) {

        // Simple debounce utility to prevent spamming the API on every keystroke
        function debounce(func, wait) {
            let timeout;
            return function (...args) {
                const later = () => {
                    clearTimeout(timeout);
                    func(...args);
                };
                clearTimeout(timeout);
                timeout = setTimeout(later, wait);
            };
        }

        return {
            init: function (vm, $scope) {
                // === State ===
                vm.showFilePicker = false;
                vm.pickerCardId = null;
                vm.pickerPath = '';
                vm.pickerEntries = [];
                vm.pickerSelected = [];
                vm.isSearchResult = false;
                vm.searchFilter = '';
                vm.existingFilesCount = 0;

                // === Methods ===
                vm.pickerToggleFile = function (path) {
                    var card = vm.findCardById(vm.pickerCardId);
                    if (!card) return;

                    // Always normalize to an array
                    if (!Array.isArray(card.attached)) card.attached = [];

                    var isAttached = card.attached.includes(path);

                    if (isAttached) {
                        // Remove from selected UI state
                        var idx = vm.pickerSelected.indexOf(path);
                        if (idx !== -1) vm.pickerSelected.splice(idx, 1);

                        // Remove from card model
                        var idxAttached = card.attached.indexOf(path);
                        if (idxAttached !== -1) card.attached.splice(idxAttached, 1);
                    } else {
                        // Add to selected UI state and card model
                        vm.pickerSelected.push(path);
                        card.attached.push(path);
                    }

                    vm.saveCards();
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
                    vm.existingFilesCount = 0;

                    // Pre-populate selected state for files already attached to the card
                    if (cardId && vm.state) {
                        var card = vm.findCardById(cardId);
                        if (card && card.attached) {
                            if (Array.isArray(card.attached)) {
                                for (let x = 0; x < card.attached.length; x++) {
                                    if (card.attached[x]) {
                                        var idx = vm.pickerSelected.indexOf(card.attached[x]);
                                        if (idx === -1) vm.pickerSelected.push(card.attached[x]);
                                    }
                                }
                                vm.existingFilesCount = card.attached.length;
                            }
                        }
                    }

                    $timeout(function () {
                        var searchInput = document.getElementById('attachFilePickerSearchInput');
                        if (searchInput) {
                            searchInput.focus();
                        }
                    }, 10);
                };

                vm.isFileAttached = function (filePath) {
                    if (!vm.pickerCardId || !vm.state) return false;

                    var card = vm.findCardById(vm.pickerCardId);
                    if (!card) return false;

                    var attached = card.attached;
                    if (Array.isArray(attached)) return attached.indexOf(filePath) !== -1;
                    if (attached) return attached === filePath;
                    return false;
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
                    }, function () {
                        vm.pickerEntries = [];
                    });
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

                // Debounced version of onSearchChange to prevent rapid successive searches
                vm.debouncedOnSearchChange = debounce(function () {
                    vm.loadPickerEntries();
                }, 300);

                vm.onSearchChange = function () {
                    vm.debouncedOnSearchChange();
                };

                vm.clearSearch = function () {
                    vm.searchFilter = '';
                    vm.loadPickerEntries();
                };
            }
        };
    }]);