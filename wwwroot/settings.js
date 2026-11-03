// settings.mixin.js
angular.module('kanbanApp')
    .factory('SettingsMixin', ['$http', '$window', '$timeout', function ($http, $window, $timeout) {
        const SETTINGS_KEY = 'weaverconfig.settings';

        var DEFAULT_THEME = {
            '--bg': '#071025', '--surface': '#0b1220', '--panel': '#071322',
            '--muted': '#9fb3c8', '--text': '#e6eef6', '--accent': '#06b6d4',
            '--accent-2': '#7c3aed', '--success': '#4ade80', '--warning': '#fbbf24',
            '--error': '#f87171'
        };

        // NOTE: DEFAULT_THEME must stay declared above PRESET_THEMES — 'Default'
        // below snapshots it by value so the reset preset can never drift from
        // the actual default palette (and isPresetActive('Default') keeps
        // matching via themeEquals against DEFAULT_THEME's keys).
        var PRESET_THEMES = {
            // 'Default' resets to the stock palette so users can one-click back
            // to the out-of-the-box look.
            'Default': Object.assign({}, DEFAULT_THEME),
            'Dracula': {
                '--bg': '#282a36', '--surface': '#2d2f3e', '--panel': '#2e394e',
                '--muted': '#6272a4', '--text': '#f8f8f2', '--accent': '#bd93f9',
                '--accent-2': '#ff79c6', '--success': '#50fa7b', '--warning': '#f1fa8c',
                '--error': '#ff5555'
            },
            'Nord': {
                '--bg': '#2e3440', '--surface': '#3b4252', '--panel': '#434c5e',
                '--muted': '#81a1c1', '--text': '#eceff4', '--accent': '#88c0d0',
                '--accent-2': '#b48ead', '--success': '#a3be8c', '--warning': '#ebcb8b',
                '--error': '#bf616a'
            },
            'Solarized': {
                '--bg': '#002b36', '--surface': '#073642', '--panel': '#0a4a54',
                '--muted': '#657b83', '--text': '#93a1a1', '--accent': '#268bd2',
                '--accent-2': '#d33682', '--success': '#859900', '--warning': '#b58900',
                '--error': '#dc322f'
            },
            'Catppuccin': {
                '--bg': '#1e1e2e', '--surface': '#181825', '--panel': '#11111b',
                '--muted': '#a6adc8', '--text': '#cdd6f4', '--accent': '#89b4fa',
                '--accent-2': '#f5c2e7', '--success': '#a6e3a1', '--warning': '#f9e2af',
                '--error': '#f38ba8'
            },
            'Tokyo Night': {
                '--bg': '#0d0f1c', '--surface': '#13152a', '--panel': '#1a1b2e',
                '--muted': '#565f89', '--text': '#c0caf5', '--accent': '#7aa2f7',
                '--accent-2': '#bb9af7', '--success': '#9ece6a', '--warning': '#e0af68',
                '--error': '#f7768e'
            },
            'One Dark': {
                '--bg': '#282c34', '--surface': '#21252b', '--panel': '#2c313a',
                '--muted': '#5c6370', '--text': '#abb2bf', '--accent': '#61afef',
                '--accent-2': '#c678dd', '--success': '#98c379', '--warning': '#e5c07b',
                '--error': '#e06c75'
            },
            'Monokai': {
                '--bg': '#272822', '--surface': '#1e1f1c', '--panel': '#2d2e2b',
                '--muted': '#75715e', '--text': '#f8f8f2', '--accent': '#66d9ef',
                '--accent-2': '#ae81ff', '--success': '#a6e22e', '--warning': '#e6db74',
                '--error': '#f92672'
            },
            'Gruvbox Dark': {
                '--bg': '#282828', '--surface': '#1d2021', '--panel': '#32302f',
                '--muted': '#a89984', '--text': '#ebdbb2', '--accent': '#83a598',
                '--accent-2': '#d3869b', '--success': '#b8bb26', '--warning': '#fabd2f',
                '--error': '#fb4934'
            },
            'Rosé Pine': {
                '--bg': '#191724', '--surface': '#1f1d2e', '--panel': '#26233a',
                '--muted': '#6e6a86', '--text': '#e0def4', '--accent': '#c4a7e7',
                '--accent-2': '#ebbcba', '--success': '#9ccfd8', '--warning': '#f6c177',
                '--error': '#eb6f92'
            },
            'Night Owl': {
                '--bg': '#011627', '--surface': '#0b2942', '--panel': '#122d42',
                '--muted': '#607a93', '--text': '#d6deeb', '--accent': '#82aaff',
                '--accent-2': '#c792ea', '--success': '#addb67', '--warning': '#f78c6c',
                '--error': '#ef5350'
            },
            'Synthwave 84': {
                '--bg': '#241b2f', '--surface': '#1a1625', '--panel': '#2c2340',
                '--muted': '#7d6ba8', '--text': '#e0e0ff', '--accent': '#ff7edb',
                '--accent-2': '#36f9f6', '--success': '#53fc9f', '--warning': '#fcee4b',
                '--error': '#fe4450'
            },
            'Cyberpunk': {
                '--bg': '#0d0221', '--surface': '#10002b', '--panel': '#240046',
                '--muted': '#7b2cbf', '--text': '#e0aaff', '--accent': '#ff3864',
                '--accent-2': '#00f5d4', '--success': '#01fdf6', '--warning': '#fcee4b',
                '--error': '#ff1e56'
            },
            'GitHub Dark': {
                '--bg': '#0d1117', '--surface': '#161b22', '--panel': '#010409',
                '--muted': '#8b949e', '--text': '#c9d1d9', '--accent': '#58a6ff',
                '--accent-2': '#bc8cff', '--success': '#3fb950', '--warning': '#d29922',
                '--error': '#f85149'
            },
            'Material Ocean': {
                '--bg': '#0f111a', '--surface': '#090a0f', '--panel': '#131620',
                '--muted': '#6f7f92', '--text': '#c0c5ce', '--accent': '#8fa1b3',
                '--accent-2': '#b48ead', '--success': '#a3be8c', '--warning': '#ebcb8b',
                '--error': '#bf616a'
            },
            'Everforest': {
                '--bg': '#2d353b', '--surface': '#272e33', '--panel': '#343f44',
                '--muted': '#859289', '--text': '#d3c6aa', '--accent': '#7fbbb3',
                '--accent-2': '#d699b6', '--success': '#a7c080', '--warning': '#dbbc7f',
                '--error': '#e67e80'
            },
            'Palenight': {
                '--bg': '#292d3e', '--surface': '#202331', '--panel': '#32374d',
                '--muted': '#676e95', '--text': '#a6accd', '--accent': '#82aaff',
                '--accent-2': '#c792ea', '--success': '#c3e88d', '--warning': '#ffcb6b',
                '--error': '#f07178'
            },
            'Horizon': {
                '--bg': '#1c1e26', '--surface': '#16161e', '--panel': '#232530',
                '--muted': '#6c6f93', '--text': '#e3e6ee', '--accent': '#26bbd9',
                '--accent-2': '#e95678', '--success': '#29d398', '--warning': '#fab795',
                '--error': '#e95678'
            },
            'Ayu Mirage': {
                '--bg': '#1f2430', '--surface': '#191e2a', '--panel': '#242936',
                '--muted': '#707a8c', '--text': '#cbccc6', '--accent': '#73d0ff',
                '--accent-2': '#ffcc66', '--success': '#aad94c', '--warning': '#ffb454',
                '--error': '#f07178'
            }
        };

        function mergeTheme(themeColors) {
            var merged = {};
            Object.keys(DEFAULT_THEME).forEach(function (k) { merged[k] = DEFAULT_THEME[k]; });
            if (themeColors) {
                Object.keys(themeColors).forEach(function (k) {
                    if (merged.hasOwnProperty(k) && themeColors[k]) merged[k] = themeColors[k];
                });
            }
            return merged;
        }

        function isHexColor(v) {
            return typeof v === 'string' && /^(#[0-9a-f]{3,4}|#[0-9a-f]{6}|#[0-9a-f]{8})$/i.test(v.trim());
        }

        // Returns the keys of a parsed theme object that look like real color
        // variables (--name: hex). Shared by the import path and the live
        // paste-preview so both agree on what is importable.
        function validThemeSourceKeys(parsed) {
            return Object.keys(parsed || {}).filter(function (k) {
                return /^--[a-z0-9-]+$/i.test(k) && isHexColor(parsed[k]);
            });
        }

        function applyTheme(el, themeColors) {
            if (!el) el = document.documentElement;
            Object.keys(themeColors).forEach(function (k) {
                el.style.setProperty(k, themeColors[k]);
            });
        }

        function normalizeProjects(raw) {
            return raw.map(function (p) {
                var maxSug = p.MaxSuggestionsPerCard;
                return { Name: p.Name || p.name, Path: p.Path || p.path, Description: p.Description || p.description || '', BuildCommands: p.buildCommands || p.BuildCommands || '', SuggestionContextDepth: p.SuggestionContextDepth || p.suggestionContextDepth || 'full', IdleSuggestions: p.IdleSuggestions !== false, MaxSuggestionsPerCard: (typeof maxSug === 'number' && maxSug >= 0 && maxSug <= 4) ? Math.round(maxSug) : 3 };
            });
        }

        return {
            init: function (vm, $scope) {
                // State
                vm.selectedProject = '';
                vm.archiveCardCount = 0;
                vm.selfImprovingCardCount = 0;
                vm.projects = [];
                vm.defaultProject = '';
                vm.settingsDefaultProject = '';
                // Mirror of the SELECTED project's per-project suggestion settings, edited from
                // the Settings panel (these apply to the currently selected project, not globally).
                vm.settingsSuggestionContextDepth = 'full';
                vm.settingsIdleSuggestions = true;
                vm.settingsMaxSuggestionsPerCard = 3;
                vm.autoQueue = true;
                // The "Weaver Benchmarks" project (auto-created by /api/benchmark/ensure-project)
                // gets a 🎯 badge in the header project picker chip + dropdown so it stands out
                // from the user's real repos. Matches by name (works before any benchmark ran,
                // since _benchmarkProjectPath is only set after the first ensure call) or by the
                // resolved benchmark root path.
                vm._normProjPath = function (s) {
                    if (!s) return '';
                    return String(s).replace(/\\/g, '/').replace(/\/+$/g, '').toLowerCase();
                };
                vm.isBenchmarkProject = function (p) {
                    if (!p) return false;
                    var name = (typeof p === 'string') ? '' : (p.Name || p.name || '');
                    var path = (typeof p === 'string') ? p : (p.Path || p.path || '');
                    if (/weaver\s*benchmarks/i.test(name)) return true;
                    return !!(vm._benchmarkProjectPath && vm._normProjPath(path) === vm._normProjPath(vm._benchmarkProjectPath));
                };
                vm.projectLabel = function (p) {
                    if (!p) return '';
                    return (vm.isBenchmarkProject(p) ? '🎯 ' : '') + (p.Name || p.name || '');
                };
                // Self-improving cards must be physically started by the user. Once armed
                // (selfImprovingAgentActive), they cycle 1-by-1 forever while no regular
                // card (todo/doing/done) is active.
                vm.selfImprovingAgentActive = false;
                vm.useVSCodeInsteadOfIDE = false;

                // Terminal/Config settings
                vm.llamaUrl = 'http://localhost:8080';
                vm.llamaModel = 'medgemma:4b';
                vm.llamaEndpoints = [];
                vm.terminalApprovalMode = 'approveAll';
                vm.approvedTerminalRoots = [];
                vm.disallowedTerminalRoots = [];
                vm.approvedTerminalRootsText = '';
                vm.disallowedTerminalRootsText = '';
                vm.maxFileContextChars = 24000;
                vm.maxFullFileTokens = 4096;
                vm.maxContextChars = 22000;
                vm.fileBodyTruncationChars = 8000;
                vm.buildOutputTailChars = 8000;
                vm.defaultMaxTokens = 2048;
                vm.includeProjectSkeleton = true;
                vm.includeEditKnowledge = false;
                vm.compactThinkingContext = true;
                vm.summarizeDiffContext = true;
                vm.diffContextSummaryChars = 6000;
                vm.llmTimeoutMinutes = 0;
                vm.llmInfiniteTimeout = true;
                vm.onLlmInfiniteToggle = function () {
                    if (!vm.llmInfiniteTimeout && vm.llmTimeoutMinutes < 5) vm.llmTimeoutMinutes = 5;
                };
                vm.buildCommands = "";
                vm.prByDefault = false;
                // StrictVerifier project default: null = legacy, true = strict by default for new
                // runs, false = relaxed by default (deterministic issues non-blocking). A card's
                // explicit STRICT checkbox (binary: checked=true/unchecked=null) always wins; the
                // relaxed(false) state is only reachable here / via the API.
                vm.defaultStrictVerifier = null;
                vm.themeColors = {};
                vm.presetThemeList = Object.keys(PRESET_THEMES);
                vm.savedThemes = [];
                vm.newThemeName = '';
                vm.themeImportText = '';
                vm.themeTransferMsg = '';
                vm.themeShareOpen = false;
                vm.currentThemeJson = function () {
                    return JSON.stringify(vm.themeColors || {}, null, 2);
                };
                vm.themeJsonPreview = vm.currentThemeJson();
                $scope.$watch(function () { return vm.currentThemeJson(); }, function (v) {
                    vm.themeJsonPreview = v;
                });
                vm.copyCurrentThemeJson = function () {
                    if (navigator.clipboard && navigator.clipboard.writeText) {
                        navigator.clipboard.writeText(vm.currentThemeJson()).then(function () {
                            vm.themeTransferMsg = '✓ Theme JSON copied to clipboard';
                            $timeout(function () { vm.themeTransferMsg = ''; }, 2500);
                        }, function () {
                            vm._copyFallback(vm.currentThemeJson());
                            vm.themeTransferMsg = '✓ Theme JSON copied to clipboard';
                            $timeout(function () { vm.themeTransferMsg = ''; }, 2500);
                        });
                    } else {
                        vm._copyFallback(vm.currentThemeJson());
                        vm.themeTransferMsg = '✓ Theme JSON copied to clipboard';
                        $timeout(function () { vm.themeTransferMsg = ''; }, 2500);
                    }
                };
                vm.ideThemeFilter = '';
                vm.ideTheme = 'weaver-dark';
                // 28 CodeMirror themes (all verified against the CodeMirror 5.65.17
                // CDN). `dark: false` marks light themes so the picker can badge them.
                vm.ideThemeList = [
                    { name: 'Default (Weaver Dark)', value: 'weaver-dark', dark: true },
                    { name: 'Monokai', value: 'monokai', dark: true },
                    { name: 'Dracula', value: 'dracula', dark: true },
                    { name: 'Material', value: 'material', dark: true },
                    { name: 'Darcula', value: 'darcula', dark: true },
                    { name: 'Seti', value: 'seti', dark: true },
                    { name: 'Tomorrow Night Eighties', value: 'tomorrow-night-eighties', dark: true },
                    { name: 'Ambiance', value: 'ambiance', dark: true },
                    { name: '3024 Night', value: '3024-night', dark: true },
                    { name: 'Blackboard', value: 'blackboard', dark: true },
                    { name: 'Cobalt', value: 'cobalt', dark: true },
                    { name: 'Gruvbox Dark', value: 'gruvbox-dark', dark: true },
                    { name: 'Lucario', value: 'lucario', dark: true },
                    { name: 'Material Darker', value: 'material-darker', dark: true },
                    { name: 'Material Ocean', value: 'material-ocean', dark: true },
                    { name: 'Material Palenight', value: 'material-palenight', dark: true },
                    { name: 'Nord', value: 'nord', dark: true },
                    { name: 'Oceanic Next', value: 'oceanic-next', dark: true },
                    { name: 'Railscasts', value: 'railscasts', dark: true },
                    { name: 'Twilight', value: 'twilight', dark: true },
                    { name: 'Vibrant Ink', value: 'vibrant-ink', dark: true },
                    { name: 'Zenburn', value: 'zenburn', dark: true },
                    { name: 'Eclipse (light)', value: 'eclipse', dark: false },
                    { name: 'IntelliJ IDEA (light)', value: 'idea', dark: false },
                    { name: 'Neo (light)', value: 'neo', dark: false },
                    { name: 'Paraiso Light', value: 'paraiso-light', dark: false },
                    { name: 'XQ Light', value: 'xq-light', dark: false },
                    { name: 'Yeti (light)', value: 'yeti', dark: false }
                ];

                vm.settingsTab = 'appearance';
                vm.showProjectOptions = false;
                vm.showEditProjectsPanel = false;
                vm.showSettingsPanel = false;
                // Set when the user edits the BugHosted credential fields this session —
                // lets saveSettings tell the server "empty means delete", not "field omitted".
                vm._bughostedCredsEdited = false;
                vm.showDiscordPanel = false;
                vm.newProjectName = '';
                vm.newProjectPath = '';
                vm.newProjectDescription = '';
                vm.newProjectBuildCommands = '';
                vm.fileHintsData = [];
                vm.emailAccounts = [];
                vm.toolList = [
                    { key: '_explore', label: 'Explore files for reference', enabled: true },
                    { key: '_discover', label: 'Project-wide context search (BM25 + AI)', enabled: true },
                    { key: '_command', label: 'Run terminal commands', enabled: true },
                    { key: '_create_file', label: 'Create new files', enabled: true },
                    { key: '_sql_migration', label: 'SQL migration files', enabled: true },
                    { key: '_web_search', label: 'Web search', enabled: true },
                    { key: '_web_fetch', label: 'Fetch URLs', enabled: true },
                    { key: '_git', label: 'Git operations', enabled: true },
                    { key: '_rename_file', label: 'Rename files', enabled: true },
                    { key: '_delete_file', label: 'Delete files', enabled: true },
                    { key: '_show', label: 'Display text to user', enabled: true }
                ];
                vm.appVersion = null;
                vm.updating = false;

                function loadLocalSettings() {
                    try {
                        var raw = $window.localStorage.getItem(SETTINGS_KEY);
                        if (raw) {
                            var s = JSON.parse(raw);
                            vm.autoQueue = s.autoQueue !== false;
                            if (typeof s.selfImprovingAgentActive === 'boolean') vm.selfImprovingAgentActive = s.selfImprovingAgentActive;
                            if (typeof s.ideMinimapVisible === 'boolean') vm._savedIdeMinimapVisible = s.ideMinimapVisible;
                            if (typeof s.ideShowHiddenEntries === 'boolean') vm._savedIdeShowHiddenEntries = s.ideShowHiddenEntries;
                            if (s.expandedDirsByProject && typeof s.expandedDirsByProject === 'object') vm._savedExpandedDirsByProject = s.expandedDirsByProject;
                            // Persistent workspace: kanban column widths + visibility and
                            // floating-panel geometry all live in this store so a reload
                            // restores the exact layout. Stashed on vm._saved* so the
                            // feature mixins (Kanban/IDE/Notes, which init after Settings)
                            // can read them synchronously.
                            if (s.kanbanColWidths && typeof s.kanbanColWidths === 'object') vm._savedKanbanColWidths = s.kanbanColWidths;
                            if (typeof s.showTodo === 'boolean') vm.showTodo = s.showTodo;
                            if (typeof s.showDoing === 'boolean') vm.showDoing = s.showDoing;
                            if (typeof s.showDone === 'boolean') vm.showDone = s.showDone;
                            if (typeof s.showArchived === 'boolean') vm.showArchived = s.showArchived;
                            if (typeof s.showSelfImproving === 'boolean') vm.showSelfImproving = s.showSelfImproving;
                            if (s.idePanel && typeof s.idePanel === 'object') vm._savedIdePanel = s.idePanel;
                            if (s.notesPanel && typeof s.notesPanel === 'object') vm._savedNotesPanel = s.notesPanel;
                        }
                    } catch (e) { }
                }
                loadLocalSettings();
                vm.persistSelfImprovingAgent = function () {
                    try {
                        var raw = $window.localStorage.getItem(SETTINGS_KEY);
                        var s = raw ? JSON.parse(raw) : {};
                        s.selfImprovingAgentActive = vm.selfImprovingAgentActive === true;
                        $window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(s));
                    } catch (e) { }
                };

                function saveLocalSettings() {
                    try {
                        var raw = $window.localStorage.getItem(SETTINGS_KEY);
                        var s = raw ? JSON.parse(raw) : {};
                        s.autoQueue = vm.autoQueue;
                        s.selfImprovingAgentActive = vm.selfImprovingAgentActive === true;
                        s.ideMinimapVisible = vm.ide ? vm.ide.minimapVisible !== false : true;
                        s.ideShowHiddenEntries = !!(vm.ide && vm.ide.showHiddenEntries);
                        // Persistent workspace: capture the live kanban layout + panel
                        // geometry every save so all of it survives a reload.
                        s.kanbanColWidths = vm._kanbanColWidths || {};
                        s.showTodo = vm.showTodo === true;
                        s.showDoing = vm.showDoing === true;
                        s.showDone = vm.showDone === true;
                        s.showArchived = vm.showArchived === true;
                        s.showSelfImproving = vm.showSelfImproving === true;
                        if (vm.ide) s.idePanel = { left: vm.ide.left, top: vm.ide.top, width: vm.ide.width, height: vm.ide.height };
                        if (vm.notes) s.notesPanel = { left: vm.notes.left, top: vm.notes.top, width: vm.notes.width, height: vm.notes.height };
                        $window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(s));
                    } catch (e) { }
                }

                vm.persistExpandedDirs = function () {
                    try {
                        var raw = $window.localStorage.getItem(SETTINGS_KEY);
                        var s = raw ? JSON.parse(raw) : {};
                        if (!s.expandedDirsByProject || typeof s.expandedDirsByProject !== 'object') s.expandedDirsByProject = {};
                        var dirs = {};
                        Object.keys(vm._expandedDirs || {}).forEach(function (k) {
                            if (k && k.indexOf('__') !== 0 && vm._expandedDirs[k]) dirs[k] = true;
                        });
                        s.expandedDirsByProject[vm.selectedProject || ''] = dirs;
                        $window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(s));
                    } catch (e) { }
                };

                // Lightweight single-key write (no full config round-trip) so the
                // kanban column widths/visibility and floating-panel geometry survive
                // a reload — the persistent-workspace counterpart to saveLocalSettings.
                vm.persistWorkspaceLayout = function () {
                    try {
                        var raw = $window.localStorage.getItem(SETTINGS_KEY);
                        var s = raw ? JSON.parse(raw) : {};
                        s.kanbanColWidths = vm._kanbanColWidths || {};
                        s.showTodo = vm.showTodo === true;
                        s.showDoing = vm.showDoing === true;
                        s.showDone = vm.showDone === true;
                        s.showArchived = vm.showArchived === true;
                        s.showSelfImproving = vm.showSelfImproving === true;
                        if (vm.ide) s.idePanel = { left: vm.ide.left, top: vm.ide.top, width: vm.ide.width, height: vm.ide.height };
                        if (vm.notes) s.notesPanel = { left: vm.notes.left, top: vm.notes.top, width: vm.notes.width, height: vm.notes.height };
                        $window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(s));
                    } catch (e) { }
                };

                vm.countArchivedCards = function () {
                    if (!vm.state || !vm.state.archived) { vm.archiveCardCount = 0; return; }
                    try {
                        if (Array.isArray(vm.state.archived)) {
                            vm.archiveCardCount = vm.state.archived.filter(function (card) { return card.filePath === vm.selectedProject; }).length;
                        } else if (typeof vm.state.archived === 'object') {
                            var archivedData = vm.state.archived[vm.selectedProject];
                            vm.archiveCardCount = Array.isArray(archivedData) ? archivedData.length : 0;
                        } else { vm.archiveCardCount = 0; }
                    } catch (e) { console.log("CountArchivedCards error", e); }
                };

                vm.loadConfig = function (project) {
                    return $http.get('/api/config').then(function (resp) {
                        try {
                            var cfg = resp.data || {};
                            var raw = (cfg.projects && cfg.projects.length) ? cfg.projects : [{ Name: 'Project Alpha', Path: '../project-alpha' }];
                            vm.projects = normalizeProjects(raw);
                            vm.selectedProject = project || cfg.defaultProject || (vm.projects.length ? vm.projects[0].Path : '');
                            vm.defaultProject = project || cfg.defaultProject;

                            if (typeof cfg.showTerminal === 'boolean') vm.showTerminal = cfg.showTerminal;
                            if (typeof cfg.showAI === 'boolean') vm.showAI = cfg.showAI;
                            if (typeof cfg.showIDE === 'boolean') vm.showIDE = cfg.showIDE;
                            if (typeof cfg.showCalendar === 'boolean') vm.showCalendar = cfg.showCalendar;
                            if (typeof cfg.showKanban === 'boolean') vm.showKanban = cfg.showKanban;
                            if (typeof cfg.showNotes === 'boolean') vm.showNotes = cfg.showNotes;
                            if (typeof cfg.showMeeting === 'boolean') vm.showMeeting = cfg.showMeeting;
                            if (typeof cfg.meetingVolume === 'number') vm.meetingVolume = Math.max(0, Math.min(100, cfg.meetingVolume));
                            else if (typeof cfg.meetingMuted === 'boolean') vm.meetingVolume = cfg.meetingMuted ? 0 : 70;
                            if (vm.applyMeetingVolume) vm.applyMeetingVolume();
                            // Font sizes sync across devices via the backend config; mirror
                            // them into localStorage so reloads show them before the async
                            // config load resolves. Backend wins over the local cache.
                            if (cfg.fontSizes && typeof cfg.fontSizes === 'object') {
                                var _fs = cfg.fontSizes;
                                if (typeof _fs.log === 'number' && _fs.log >= 6 && _fs.log <= 32) vm.logFontSize = _fs.log;
                                if (typeof _fs.llm === 'number' && _fs.llm >= 6 && _fs.llm <= 32) vm.llmFontSize = _fs.llm;
                                if (typeof _fs.plan === 'number' && _fs.plan >= 6 && _fs.plan <= 32) vm.planFontSize = _fs.plan;
                                if (typeof _fs.metaplan === 'number' && _fs.metaplan >= 6 && _fs.metaplan <= 32) vm.metaPlanFontSize = _fs.metaplan;
                                try {
                                    $window.localStorage.setItem('weaver.font.log', String(vm.logFontSize));
                                    $window.localStorage.setItem('weaver.font.llm', String(vm.llmFontSize));
                                    $window.localStorage.setItem('weaver.font.plan', String(vm.planFontSize));
                                    $window.localStorage.setItem('weaver.font.metaplan', String(vm.metaPlanFontSize));
                                } catch (e) { }
                            }
                            if (cfg.meetingPanel && typeof cfg.meetingPanel === 'object') {
                                vm._meetingPanelCfg = cfg.meetingPanel;
                                if (vm.meeting) {
                                    if (typeof cfg.meetingPanel.left === 'number') vm.meeting.left = cfg.meetingPanel.left;
                                    if (typeof cfg.meetingPanel.top === 'number') vm.meeting.top = cfg.meetingPanel.top;
                                    if (typeof cfg.meetingPanel.width === 'number') vm.meeting.width = cfg.meetingPanel.width;
                                    if (typeof cfg.meetingPanel.height === 'number') vm.meeting.height = cfg.meetingPanel.height;
                                    if (vm._clampMeetingPanel) vm._clampMeetingPanel();
                                    // A stale saved position could still sit on the
                                    // Agent panel / panel columns — nudge it away.
                                    if (vm.showMeeting && vm._dodgeFloatingPanel) vm._dodgeFloatingPanel(vm.meeting, { selfCls: 'meeting-floating-panel', margin: 10 });
                                }
                            }
                            if (typeof cfg.useVSCodeInsteadOfIDE === 'boolean') vm.useVSCodeInsteadOfIDE = cfg.useVSCodeInsteadOfIDE;
                            if (typeof cfg.ideTheme === 'string') vm.ideTheme = cfg.ideTheme;
                            if (typeof cfg.ideMinimapVisible === 'boolean' && vm.ide) vm.ide.minimapVisible = cfg.ideMinimapVisible;
                            if (typeof cfg.prByDefault === 'boolean') vm.prByDefault = cfg.prByDefault;
                            if (typeof cfg.defaultStrictVerifier === 'boolean') vm.defaultStrictVerifier = cfg.defaultStrictVerifier;
                            vm.llamaUrl = cfg.llamaUrl || "http://localhost:8080";
                            vm.llamaModel = cfg.llamaModel || "medgemma:4b";
                            vm.llamaEndpoints = (cfg.llamaEndpoints || []).map(function (e) { return { id: e.id || ('ep-' + Math.random().toString(36).slice(2, 9)), name: e.name || '', url: e.url || '', model: e.model || '' }; });
                            vm.loadEndpointHealth();
                            // Auto-populate the model dropdown from the configured server so
                            // the user sees their actual models when they open Settings (not a
                            // stale free-text default). Fails silently when the server is down.
                            vm.refreshModels();
                            vm.terminalApprovalMode = cfg.terminalApprovalMode || 'approveAll';
                            vm.approvedTerminalRoots = cfg.approvedTerminalRoots || [];
                            vm.approvedTerminalRootsText = vm.approvedTerminalRoots.join(', ');
                            vm.disallowedTerminalRoots = cfg.disallowedTerminalRoots || [];
                            vm.disallowedTerminalRootsText = vm.disallowedTerminalRoots.join(', ');
                            vm.maxFileContextChars = typeof cfg.maxFileContextChars === 'number' ? cfg.maxFileContextChars : 24000;
                            vm.maxFullFileTokens = typeof cfg.maxFullFileTokens === 'number' ? cfg.maxFullFileTokens : 4096;
                            vm.maxContextChars = typeof cfg.maxContextChars === 'number' ? cfg.maxContextChars : 22000;
                            vm.fileBodyTruncationChars = typeof cfg.fileBodyTruncationChars === 'number' ? cfg.fileBodyTruncationChars : 8000;
                            vm.buildOutputTailChars = typeof cfg.buildOutputTailChars === 'number' ? cfg.buildOutputTailChars : 8000;
                            vm.defaultMaxTokens = typeof cfg.defaultMaxTokens === 'number' ? cfg.defaultMaxTokens : 2048;
                            vm.includeProjectSkeleton = cfg.includeProjectSkeleton === true;
                            vm.includeEditKnowledge = cfg.includeEditKnowledge === true;
                            vm.extendThinking = cfg.extendThinking !== false;
                            vm.thinkingMaxTokens = typeof cfg.thinkingMaxTokens === 'number' ? cfg.thinkingMaxTokens : 4096;
                            vm.compactThinkingContext = cfg.compactThinkingContext !== false;
                            vm.summarizeDiffContext = cfg.summarizeDiffContext !== false;
                            vm.diffContextSummaryChars = typeof cfg.diffContextSummaryChars === 'number' ? cfg.diffContextSummaryChars : 6000;
                            vm.llmTimeoutMinutes = typeof cfg.llmTimeoutMinutes === 'number' && cfg.llmTimeoutMinutes > 0 ? Math.max(5, cfg.llmTimeoutMinutes) : 0;
                            vm.llmInfiniteTimeout = vm.llmTimeoutMinutes <= 0;

                            vm.enabledTools = cfg.enabledTools || [];
                            vm.toolList.forEach(function (t) { t.enabled = vm.enabledTools.length === 0 || vm.enabledTools.indexOf(t.key) !== -1; });

                            vm.emailAccounts = (cfg.emailAccounts || []).map(function (a) {
                                return { imapServer: a.imapServer || '', imapPort: a.imapPort || 993, useSsl: a.useSsl !== false, username: a.username || '', password: a.password || '', label: a.label || '', showAppPasswordInstructions: false, testing: false, testResult: null };
                            });
                            if (vm.emailAccounts.length === 0 && (cfg.emailUsername || cfg.emailImapServer)) {
                                vm.emailAccounts.push({ imapServer: cfg.emailImapServer || '', imapPort: cfg.emailImapPort || 993, useSsl: cfg.emailUseSsl !== false, username: cfg.emailUsername || '', password: cfg.emailPassword || '', label: '', showAppPasswordInstructions: false, testing: false, testResult: null });
                            }
                            vm.bughostedUrl = cfg.bughostedUrl || '';
                            vm.bughostedUsername = cfg.bughostedUsername || '';
                            vm.bughostedPassword = cfg.bughostedPassword || '';
                            vm.bughostedHeartbeatEnabled = cfg.bughostedHeartbeatEnabled || false;
                            vm.bughostedShareRank = cfg.bughostedShareRank === true;
                            vm.bughostedShareSkeleton = cfg.bughostedShareSkeleton === true;
                            vm.themeColors = mergeTheme(cfg.themeColors);
                            vm.savedThemes = (cfg.savedThemes || []).map(function (t) { return { name: t.name || 'Untitled', colors: mergeTheme(t.colors), _editing: false, _editName: '' }; });
                            applyTheme(null, vm.themeColors);
                            if (vm.ide && vm.ide.showSidebar && vm.loadFilePickerEntries) {
                                $timeout(function () { vm.loadFilePickerEntries(); }, 100);
                            }
                        } catch (e) { console.log("Loading config error", e); }
                    }, function () {
                        vm.projects = normalizeProjects([{ Name: 'Default', Path: '..' }]);
                        vm.selectedProject = '..'; vm.defaultProject = '..';
                    });
                };

                vm.markBughostedCredsEdited = function () { vm._bughostedCredsEdited = true; };

                vm.saveSettings = function (skipCloseSettingsPanel = false) {
                    saveLocalSettings();
                    $http.get('/api/config').then(function (resp) {
                        var cfg = resp.data || { projects: vm.projects };
                        cfg.projects = cfg.projects || vm.projects;
                        cfg.defaultProject = vm.settingsDefaultProject || vm.defaultProject;
                        cfg.llamaUrl = vm.llamaUrl || "http://localhost:8080";
                        cfg.llamaModel = vm.llamaModel || "medgemma:4b";
                        cfg.llamaEndpoints = vm.llamaEndpoints || [];
                        cfg.terminalApprovalMode = vm.terminalApprovalMode || 'approveAll';
                        cfg.approvedTerminalRoots = (vm.approvedTerminalRootsText || '').split(',').map(function (r) { return r.trim().toLowerCase(); }).filter(Boolean);
                        cfg.disallowedTerminalRoots = (vm.disallowedTerminalRootsText || '').split(',').map(function (r) { return r.trim().toLowerCase(); }).filter(Boolean);
                        cfg.maxFileContextChars = vm.maxFileContextChars || 24000;
                        cfg.maxFullFileTokens = vm.maxFullFileTokens || 4096;
                        cfg.maxContextChars = vm.maxContextChars || 22000;
                        cfg.fileBodyTruncationChars = vm.fileBodyTruncationChars || 8000;
                        cfg.buildOutputTailChars = vm.buildOutputTailChars || 8000;
                        cfg.defaultMaxTokens = vm.defaultMaxTokens || 2048;
                        cfg.includeProjectSkeleton = vm.includeProjectSkeleton === true;
                        cfg.includeEditKnowledge = vm.includeEditKnowledge === true;
                        cfg.extendThinking = vm.extendThinking === true;
                        cfg.thinkingMaxTokens = vm.thinkingMaxTokens || 4096;
                        cfg.compactThinkingContext = vm.compactThinkingContext !== false;
                        cfg.summarizeDiffContext = vm.summarizeDiffContext !== false;
                        cfg.diffContextSummaryChars = vm.diffContextSummaryChars || 6000;
                        cfg.llmTimeoutMinutes = vm.llmInfiniteTimeout || !vm.llmTimeoutMinutes ? 0 : Math.max(5, vm.llmTimeoutMinutes);
                        cfg.showNotes = vm.showNotes === true;
                        cfg.showMeeting = vm.showMeeting === true;
                        cfg.showCalendar = vm.showCalendar === true;
                        cfg.meetingVolume = Math.round(vm.meetingVolume || 0);
                        cfg.meetingMuted = (vm.meetingVolume || 0) <= 0;
                        if (vm.meeting) cfg.meetingPanel = { left: vm.meeting.left, top: vm.meeting.top, width: vm.meeting.width, height: vm.meeting.height };
                        cfg.useVSCodeInsteadOfIDE = vm.useVSCodeInsteadOfIDE === true;
                        cfg.prByDefault = vm.prByDefault === true;
                        // Persist the strict-verifier project default explicitly: null = legacy,
                        // true = strict by default, false = relaxed. (prByDefault above was previously
                        // never explicitly written here — a toggle only survived if another setting
                        // happened to force a save AND resp.data already carried it. Explicit write
                        // fixes that for both flags.)
                        cfg.defaultStrictVerifier = (vm.defaultStrictVerifier === true || vm.defaultStrictVerifier === false) ? vm.defaultStrictVerifier : null;
                        cfg.ideTheme = vm.ideTheme || 'weaver-dark';
                        cfg.ideMinimapVisible = !!(vm.ide && vm.ide.minimapVisible);
                        cfg.fontSizes = { log: vm.logFontSize, llm: vm.llmFontSize, plan: vm.planFontSize, metaplan: vm.metaPlanFontSize };
                        cfg.emailAccounts = vm.emailAccounts.map(function (a) { return { imapServer: a.imapServer, imapPort: a.imapPort, useSsl: a.useSsl, username: a.username, password: a.password, label: a.label }; });
                        cfg.bughostedUrl = vm.bughostedUrl || '';
                        cfg.bughostedUsername = vm.bughostedUsername || '';
                        cfg.bughostedPassword = vm.bughostedPassword || '';
                        cfg.bughostedHeartbeatEnabled = vm.bughostedHeartbeatEnabled || false;
                        cfg.bughostedShareRank = vm.bughostedShareRank === true;
                        cfg.bughostedShareSkeleton = vm.bughostedShareSkeleton === true;
                        // The user edited the credential fields to empty this session — empty is
                        // authoritative, so tell the server not to restore the old credentials.
                        cfg.clearBughostedCredentials = !!(vm._bughostedCredsEdited && !vm.bughostedUsername && !vm.bughostedPassword);
                        cfg.themeColors = vm.themeColors;
                        cfg.savedThemes = vm.savedThemes.map(function (t) { return { name: t.name || 'Untitled', colors: t.colors || {} }; });
                        cfg.enabledTools = vm.toolList.filter(function (t) { return t.enabled; }).map(function (t) { return t.key; });
                        // Persist the Settings panel's suggestion controls onto the selected project
                        // (they edit the per-project DTO, so live consumers pick them up immediately).
                        var suggProj = vm.selectedProjectSuggestionDto();
                        if (suggProj) {
                            suggProj.SuggestionContextDepth = vm.settingsSuggestionContextDepth || 'full';
                            suggProj.IdleSuggestions = vm.settingsIdleSuggestions !== false;
                            var ms = vm.settingsMaxSuggestionsPerCard;
                            suggProj.MaxSuggestionsPerCard = (typeof ms === 'number' && ms >= 0 && ms <= 4) ? Math.round(ms) : 3;
                            if (Array.isArray(cfg.projects)) {
                                var normP = function (s) { return String(s || '').replace(/\\/g, '/').replace(/\/+$/g, '').toLowerCase(); };
                                var idxP = cfg.projects.findIndex(function (cp) { return normP(cp.Path || cp.path) === normP(suggProj.Path); });
                                if (idxP !== -1) {
                                    cfg.projects[idxP].SuggestionContextDepth = suggProj.SuggestionContextDepth;
                                    cfg.projects[idxP].IdleSuggestions = suggProj.IdleSuggestions;
                                    cfg.projects[idxP].MaxSuggestionsPerCard = suggProj.MaxSuggestionsPerCard;
                                }
                            }
                        }
                        return $http.post('/api/config/save', cfg);
                    }).then(function () {
                        if (vm._bughostedCredsEdited && !vm.bughostedUsername && !vm.bughostedPassword) {
                            // Credentials were deleted — also drop any live BugHosted session so
                            // nothing keeps running under the removed account.
                            if (vm.bughostedStatus === 'connected' && vm.bughostedLogout) vm.bughostedLogout();
                        }
                        vm._bughostedCredsEdited = false;
                        vm.defaultProject = vm.settingsDefaultProject || vm.defaultProject;
                        if (vm.settingsDefaultProject) vm.selectedProject = vm.settingsDefaultProject;
                        if (!skipCloseSettingsPanel) vm.closeSettingsPanel();
                    }, function (err) { $window.alert('Failed to save settings: ' + (err.data || err.statusText || err)); });
                };

                vm.addEmailAccount = function () { vm.emailAccounts.push({ imapServer: '', imapPort: 993, useSsl: true, username: '', password: '', label: '', showAppPasswordInstructions: false, testing: false, testResult: null }); };
                vm.removeEmailAccount = function (index) { vm.emailAccounts.splice(index, 1); };
                vm.addLlamaEndpoint = function () { vm.llamaEndpoints.push({ id: 'ep-' + Math.random().toString(36).slice(2, 9), name: '', url: 'http://localhost:8080', model: '' }); };
                vm.removeLlamaEndpoint = function (index) { vm.llamaEndpoints.splice(index, 1); };

                // ── Local AI server detection + model discovery ───────────────────
                // The Settings panel uses these to show a model dropdown populated from
                // the user's ACTUALLY loaded models, instead of a free-text field where
                // the default (medgemma:4b) may not match anything on their server.
                vm.availableModels = [];
                vm.detectedServers = null;
                vm.detectingServers = false;
                vm.refreshingModels = false;
                vm.swappingModel = false;
                vm.modelActive = null;

                vm.detectServers = function () {
                    vm.detectingServers = true;
                    $http.get('/api/ai/detect-servers').then(function (resp) {
                        vm.detectedServers = (resp.data && resp.data.servers) || [];
                        vm.detectingServers = false;
                        if (vm.detectedServers.length === 1) {
                            vm.selectDetectedServer(vm.detectedServers[0]);
                        }
                    }).catch(function () { vm.detectingServers = false; vm.detectedServers = []; });
                };
                vm.selectDetectedServer = function (server) {
                    vm.llamaUrl = server.url;
                    vm.availableModels = server.models || [];
                    if (vm.availableModels.length) {
                        if (!vm.availableModels.some(function (m) { return m.id === vm.llamaModel; })) {
                            vm.llamaModel = vm.availableModels[0].id;
                        }
                    }
                };
                vm.refreshModels = function () {
                    var url = vm.llamaUrl || 'http://localhost:8080';
                    vm.refreshingModels = true;
                    $http.get('/api/ai/models', { params: { url: url } }).then(function (resp) {
                        vm.availableModels = (resp.data && resp.data.models) || [];
                        vm.refreshingModels = false;
                        if (vm.availableModels.length) {
                            if (!vm.availableModels.some(function (m) { return m.id === vm.llamaModel; })) {
                                vm.llamaModel = vm.availableModels[0].id;
                            }
                        }
                    }).catch(function () { vm.refreshingModels = false; vm.availableModels = []; });
                };
                vm.onLlmUrlChange = function () {
                    vm.availableModels = [];
                    vm.detectedServers = null;
                };
                vm.onModelSelect = function () {
                    // When the user picks a different model from the dropdown, swap it on the
                    // server so it's actually loaded in VRAM. On Lemonade Server (pinned models),
                    // this unloads the current model and loads the selected one — without it,
                    // the next agent run would fail with a 409 slots_pinned_error. On Ollama/
                    // LM Studio (auto-load), the swap is a no-op that returns immediately.
                    if (!vm.llamaModel || !vm.llamaUrl) return;
                    var url = vm.llamaUrl || 'http://localhost:8080';
                    vm.swappingModel = true;
                    $http.post('/api/ai/swap-model', { url: url, model: vm.llamaModel })
                        .then(function (resp) {
                            vm.swappingModel = false;
                            if (resp.data && resp.data.success) {
                                vm.modelActive = true;
                            }
                        }).catch(function () { vm.swappingModel = false; vm.modelActive = false; });
                };
                vm.modelIsInList = function () {
                    if (!vm.availableModels || !vm.availableModels.length) return false;
                    return vm.availableModels.some(function (m) { return m.id === vm.llamaModel; });
                };
                vm.endpointLabel = function (id) {
                    if (!id) return 'Default';
                    var ep = (vm.llamaEndpoints || []).find(function (e) { return e.id === id; });
                    return ep ? (ep.name || ep.url || 'Default') : 'Default';
                };
                // Per-endpoint stream reliability (backend accumulates these from every LLM
                // call): used to badge flaky endpoints in the picker. Keyed by normalized URL.
                vm.endpointHealthMap = {};
                vm.loadEndpointHealth = function () {
                    return $http.get('/api/agent/endpoint-health').then(function (resp) {
                        var map = {};
                        (resp.data || []).forEach(function (h) {
                            map[(h.baseUrl || '').replace(/\/+$/, '')] = h;
                        });
                        vm.endpointHealthMap = map;
                    }, function () { });
                };
                vm.endpointHealthFor = function (url) {
                    if (!url) return null;
                    var key = String(url).replace(/\/+$/, '');
                    return vm.endpointHealthMap[key] || null;
                };
                // Returns { level, icon, title } for an endpoint URL, or null when there's
                // not enough data yet. Bad (🔴): >=40% stream-error rate or >=5 errors with
                // no recent success. Warn (🟡): >=15% rate or any errors while calls are few.
                // The tooltip also reports recovery effectiveness (retries that landed vs
                // still failed) so users can judge whether the self-healing is working.
                //
                // NOTE: the badge object is memoized by content. Templates bind this
                // function directly (ng-if, interpolations), and Angular dirty-checks the
                // RESULT by reference — returning a fresh object every digest made the
                // loop never settle ($rootScope:infdig). Same content → same reference.
                vm._endpointBadgeCache = {};
                vm.endpointHealthBadge = function (url) {
                    var h = vm.endpointHealthFor(url);
                    if (!h || !h.calls || h.calls < 3) return null;
                    var rate = h.errorRate || 0;
                    // Only mention recovery stats once there's actual retry activity — a fresh
                    // endpoint with zero retries shouldn't clutter every tooltip with '0/0'.
                    var rec = (h.recovered || 0) + (h.recoveryFailed || 0) > 0
                        ? ' · ♻ ' + (h.recovered || 0) + ' recovered / ' + (h.recoveryFailed || 0) + ' failed retries'
                        : '';
                    var badge;
                    if (rate >= 40 || (h.streamErrors >= 5 && !h.lastSuccessAt)) {
                        badge = { level: 'bad', icon: '🔴', title: 'Frequently drops streams: ' + h.streamErrors + '/' + h.calls + ' calls failed mid-stream (' + rate + '%)' + rec };
                    }
                    // No successful call in the last hour while errors keep coming → treat
                    // as bad, not just warn, so a sick endpoint is easy to spot.
                    else if (rate >= 15 || h.streamErrors >= 2) {
                        if (h.stale) {
                            badge = { level: 'bad', icon: '🔴', title: 'Unhealthy: no successful call in 1h+ and ' + h.streamErrors + '/' + h.calls + ' calls failed (' + rate + '%)' + rec };
                        } else {
                            badge = { level: 'warn', icon: '🟡', title: 'Some stream drops: ' + h.streamErrors + '/' + h.calls + ' calls failed (' + rate + '%)' + rec };
                        }
                    } else {
                        badge = { level: 'ok', icon: '🟢', title: 'Healthy: ' + h.streamErrors + '/' + h.calls + ' calls failed (' + rate + '%)' + rec };
                    }
                    var key = badge.level + '|' + badge.icon + '|' + badge.title;
                    if (vm._endpointBadgeCache[key]) return vm._endpointBadgeCache[key];
                    // Bounded cache — distinct titles (counts drift as data updates), so
                    // cap it and rebuild rather than leaking references forever.
                    if (Object.keys(vm._endpointBadgeCache).length > 64) vm._endpointBadgeCache = {};
                    vm._endpointBadgeCache[key] = badge;
                    return badge;
                };
                vm.endpointBadgeText = function (url) {
                    var b = vm.endpointHealthBadge(url);
                    return b ? ' ' + b.icon : '';
                };
                vm.endpointBadgeTitle = function (url) {
                    var b = vm.endpointHealthBadge(url);
                    return b ? b.title : '';
                };
                vm.checkEmailServer = function (index) {
                    var acct = vm.emailAccounts[index]; if (!acct || !acct.imapServer) return acct.showAppPasswordInstructions = false;
                    var lower = acct.imapServer.toLowerCase();
                    acct.showAppPasswordInstructions = (lower.includes('gmail.com') || lower.includes('googlemail.com')) ? 'google' : (lower.includes('outlook.com') || lower.includes('hotmail.com') || lower.includes('live.com') || lower.includes('msn.com')) ? 'microsoft' : false;
                };
                vm.testEmailConnection = function (index) {
                    var acct = vm.emailAccounts[index]; if (!acct || !acct.imapServer || !acct.username || !acct.password) return acct.testResult = { success: false, message: 'Please fill in all fields' };
                    acct.testing = true; acct.testResult = null;
                    $http.post('/api/email/test', { imapServer: acct.imapServer, imapPort: acct.imapPort, useSsl: acct.useSsl, username: acct.username, password: acct.password })
                        .then(function (response) { acct.testing = false; acct.testResult = response.data; })
                        .catch(function (error) { acct.testing = false; acct.testResult = { success: false, message: 'Connection test failed: ' + (error.data || error.statusText || 'Unknown error') }; });
                };

                vm.getProjectBuildCommands = function (projectIndex) {
                    if (!vm.projects || !vm.projects[projectIndex]) return '';
                    return vm.projects[projectIndex].BuildCommands || '';
                };

                vm.loadFileHints = function () {
                    $http.get('/api/filehints').then(function (response) {
                        try {
                            var store = typeof response.data === 'string' ? JSON.parse(response.data) : response.data;
                            if (store && store.Projects && vm.projects) {
                                vm.fileHintsData = vm.projects.map(function (p) {
                                    var proj = store.Projects[p.Path];
                                    return { projectPath: p.Path, hints: proj && proj.Hints ? proj.Hints.map(function (h) { return { keywords: (h.Keywords || []).join(', '), files: (h.Files || []).length > 0 ? h.Files.slice() : [''] }; }) : [] };
                                });
                            } else { vm.fileHintsData = []; }
                        } catch (e) { vm.fileHintsData = []; }
                    }, function () { vm.fileHintsData = vm.projects ? vm.projects.map(function (p) { return { projectPath: p.Path, hints: [] }; }) : []; });
                };

                vm.getProjectHints = function (projectIndex) {
                    if (!vm.fileHintsData) vm.fileHintsData = [];
                    if (!vm.fileHintsData[projectIndex]) {
                        var proj = (vm.projects && vm.projects[projectIndex]) ? vm.projects[projectIndex] : { Path: '' };
                        vm.fileHintsData[projectIndex] = { projectPath: proj.Path, hints: [] };
                    }
                    return vm.fileHintsData[projectIndex].hints;
                };
                vm.addHint = function (projectIndex) { vm.getProjectHints(projectIndex).push({ keywords: '', files: [''] }); };
                vm.removeHint = function (projectIndex, hintIndex) { if (vm.fileHintsData[projectIndex]) vm.fileHintsData[projectIndex].hints.splice(hintIndex, 1); };
                vm.addFileToHint = function (projectIndex, hintIndex) { if (vm.fileHintsData[projectIndex] && vm.fileHintsData[projectIndex].hints[hintIndex]) vm.fileHintsData[projectIndex].hints[hintIndex].files.push(''); };
                vm.removeFileFromHint = function (projectIndex, hintIndex, fileIndex) { if (vm.fileHintsData[projectIndex] && vm.fileHintsData[projectIndex].hints[hintIndex]) vm.fileHintsData[projectIndex].hints[hintIndex].files.splice(fileIndex, 1); };
                vm.saveFileHints = function () {
                    var payload = { Projects: {} };
                    vm.fileHintsData.forEach(function (entry) {
                        var projectKey = entry.projectPath || vm.selectedProject || vm.defaultProject || '__default__';
                        payload.Projects[projectKey] = { Hints: entry.hints.map(function (h) { return { Keywords: h.keywords.split(',').map(function (k) { return k.trim(); }).filter(Boolean), Files: h.files.filter(Boolean) }; }), AutoLearned: [] };
                    });
                    return $http.put('/api/filehints', payload).then(function () { vm.closeSettingsPanel(); }, function (err) { $window.alert('Failed to save file hints: ' + (err.data || err.statusText || err)); });
                };

                vm.toggleProjectOptions = function () { vm.showProjectOptions = !vm.showProjectOptions; };
                vm.closeOptionsOnBlur = function (event) { $timeout(function () { vm.showProjectOptions = false; $timeout(function () { vm.saveSettings(true); }, 300); }, 300); };

                // ── Rank chip → user-stats popup ──────────────────────────────
                // The header rank button (next to the Project picker) opens a small
                // popup showing the same stats the old chip row displayed inline.
                vm.showUserStats = false;
                vm.toggleUserStats = function () { vm.showUserStats = !vm.showUserStats; };
                // Explicit dismiss — the ✕ button in the popup header, so closing never
                // depends on blur/outside-click (which is fragile on touch and keyboard).
                vm.closeUserStats = function () { vm.showUserStats = false; };
                // Closing on blur must NOT fire when focus simply moved INTO the
                // stats popup (e.g. clicking a rank-ladder row to expand it — the
                // row is a plain div, so the browser transfers focus to the popup
                // container, blurring the trigger button). Only close when focus
                // truly leaves the popup area (clicking outside / Tab away).
                // Track the last mousedown so blur caused by a click INSIDE the
                // popup (where relatedTarget may be null/body, not the popup) is
                // never mistaken for an outside click.
                vm._statsMousedownInside = false;
                document.addEventListener('mousedown', function (e) {
                    var t = e && e.target;
                    vm._statsMousedownInside = !!(t && typeof t.closest === 'function' && t.closest('.user-stats-popup'));
                }, true);
                function _focusInsideStatsPopup(el) {
                    return !!(el && typeof el.closest === 'function' && el.closest('.user-stats-popup'));
                }
                vm.closeUserStatsOnBlur = function (event) {
                    var related = event && event.relatedTarget;
                    if (_focusInsideStatsPopup(related)) return;
                    $timeout(function () {
                        if (vm._statsMousedownInside) { vm._statsMousedownInside = false; return; }
                        if (_focusInsideStatsPopup(document.activeElement)) return;
                        vm.showUserStats = false;
                    }, 200);
                };
                vm.changeProject = function () { vm.loadConfig(vm.selectedProject).then(function () { vm.refreshSuggestionSettings(); $timeout(function () { vm.countArchivedCards(); vm.loadFilePickerEntries(); }, 100); }); };
                vm.openEditProjectsPanel = function () { vm.newProjectName = ''; vm.newProjectPath = ''; vm.newProjectDescription = ''; vm.settingsDefaultProject = vm.defaultProject || vm.selectedProject; vm.projects.forEach(function (p) { p._origPath = p.Path; }); vm.showEditProjectsPanel = true; };
                vm.closeEditProjectsPanel = function () { vm.saveSettings(true); vm.showEditProjectsPanel = false; };
                vm.addProjectFromPanel = function () {
                    if (!vm.newProjectName) return $window.alert('Project name is required');
                    if (!vm.newProjectPath) return $window.alert('Project path is required');
                    $http.post('/api/config/projects/add', { Name: vm.newProjectName, Path: vm.newProjectPath.replace(/\\/g, '/'), Description: vm.newProjectDescription || '', BuildCommands: vm.newProjectBuildCommands || '' })
                        .then(function () { vm.loadConfig(); vm.newProjectName = ''; vm.newProjectPath = ''; vm.newProjectDescription = ''; vm.newProjectBuildCommands = ''; }, function (err) { $window.alert('Failed to add project: ' + (err.data || err.statusText)); });
                };
                vm.saveProject = function (p) {
                    if (!p.Name || !p.Path) return $window.alert('Name and Path are required');
                    var originalPath = p._origPath || p.Path;
                    $http.get('/api/config').then(function (resp) {
                        var cfg = resp.data || { projects: [] }; cfg.projects = cfg.projects || [];
                        var idx = cfg.projects.findIndex(function (cp) { return (cp.Path || cp.path) === originalPath; });
                        if (idx === -1) return $window.alert('Project not found in config');
                        var newPath = p.Path.replace(/\\/g, '/');
                        if (newPath !== originalPath && cfg.projects.some(function (cp) { return (cp.Path || cp.path) === newPath; })) return $window.alert('A project with that path already exists');
                        cfg.projects[idx].Name = p.Name; cfg.projects[idx].Path = newPath; cfg.projects[idx].Description = p.Description || ''; cfg.projects[idx].BuildCommands = p.BuildCommands || ''; cfg.projects[idx].SuggestionContextDepth = p.SuggestionContextDepth || 'full'; cfg.projects[idx].IdleSuggestions = p.IdleSuggestions !== false; var ms = p.MaxSuggestionsPerCard; cfg.projects[idx].MaxSuggestionsPerCard = (typeof ms === 'number' && ms >= 0 && ms <= 4) ? Math.round(ms) : 3;
                        $http.post('/api/config/save', cfg).then(function () { vm.loadConfig(); }, function (err) { $window.alert('Failed to save: ' + (err.data || err.statusText)); });
                    });
                };
                vm.removeProject = function (p, event) {
                    if (event) event.stopPropagation(); if (!p || !p.Path) return;
                    if (!$window.confirm('Remove project "' + (p.Name || '') + '" (' + p.Path + ')?')) return;
                    $http.post('/api/config/projects/remove', { Path: p.Path }).then(function () { vm.loadConfig(); });
                };
                vm.openDiscordPanel = function () {
                    vm.showDiscordPanel = true; vm.loadVersion();
                    // Warm up the chitter AudioContext inside this click (a real user
                    // gesture) so hover sounds play even if the panel was opened a
                    // while ago (browser autoplay policies suspend un-gestured audio).
                    vm._ensureChitterCtx();
                    // Kick off the randomised idle eye scan.
                    vm._scheduleDiscordEyeScan();
                };
                vm.closeDiscordPanel = function () {
                    vm.showDiscordPanel = false; vm.showChangelog = false;
                    vm._cancelDiscordEyeScan();
                };
                // Eyes-follow-mouse on the big skulltula in the Discord panel. Mousemove over
                // the brand shifts each amber pupil (and its highlight) a little toward the
                // cursor within its socket; mouseleave resets it. Disabled under reduced motion.
                vm.discordEyesFollow = function (ev) {
                    var host = ev && ev.currentTarget;
                    // Hover hands control from the idle scanner to the mouse: kill any
                    // in-flight glance/blink and drop their classes so the direct
                    // transform writes below stay instant.
                    vm._discordBrandHovered = true;
                    vm._cancelDiscordEyeScan();
                    if (host) vm._discordEyeSetPupils(host, '', '');
                    if (host && host.querySelectorAll) {
                        Array.prototype.forEach.call(host.querySelectorAll('.brand-lid'), function (el) {
                            el.classList.remove('brand-blink');
                        });
                    }
                    var svg = host && host.querySelector && host.querySelector('.discord-brand-icon');
                    if (!svg) return;
                    var ctm = typeof svg.getScreenCTM === 'function' ? svg.getScreenCTM() : null;
                    if (!ctm) return;
                    var pt;
                    try {
                        if (typeof svg.createSVGPoint === 'function') {
                            pt = svg.createSVGPoint();
                            pt.x = ev.clientX;
                            pt.y = ev.clientY;
                            pt = pt.matrixTransform(ctm.inverse());
                        } else {
                            pt = new DOMPoint(ev.clientX, ev.clientY).matrixTransform(ctm.inverse());
                        }
                    } catch (e) { return; }
                    [
                        { homeX: 10.1, homeY: 10.3, maxDX: 0.8, maxDY: 0.55, cls: 'brand-pupil-left' },
                        { homeX: 13.9, homeY: 10.3, maxDX: 0.8, maxDY: 0.55, cls: 'brand-pupil-right' }
                    ].forEach(function (eye) {
                        // Small dead-zone keeps the pupils at rest when the cursor is centred
                        // on the eye so they don't jitter as the mouse crosses the logo.
                        var tx = 0, ty = 0;
                        if (Math.abs(pt.x - eye.homeX) > 0.15) {
                            tx = Math.max(-eye.maxDX, Math.min(eye.maxDX, pt.x - eye.homeX));
                        }
                        if (Math.abs(pt.y - eye.homeY) > 0.15) {
                            ty = Math.max(-eye.maxDY, Math.min(eye.maxDY, pt.y - eye.homeY));
                        }
                        var transform = (tx === 0 && ty === 0) ? '' : 'translate(' + tx + ' ' + ty + ')';
                        Array.prototype.forEach.call(host.querySelectorAll('.' + eye.cls), function (el) {
                            el.setAttribute('transform', transform);
                        });
                    });
                };
                vm.discordEyesReset = function (ev) {
                    var host = ev && ev.currentTarget;
                    if (!host || !host.querySelectorAll) return;
                    Array.prototype.forEach.call(host.querySelectorAll('.brand-pupil'), function (el) {
                        el.removeAttribute('transform');
                    });
                    // Back to idle: resume the randomised scan from a fresh pause.
                    vm._discordBrandHovered = false;
                    vm._scheduleDiscordEyeScan();
                };
                // Randomised idle eye scan: instead of a fixed CSS loop, each glance is
                // scheduled with a random pause (2.6–9s), a random direction, and a
                // random dwell before looking back — occasionally a quick double-glance
                // the other way. The scan pauses on hover (eyes-follow takes over via
                // transform attributes) and resumes on mouseleave.
                vm._discordEyeScanTimer = null;
                vm._discordBrandHovered = false;
                vm._cancelDiscordEyeScan = function () {
                    if (vm._discordEyeScanTimer) {
                        $timeout.cancel(vm._discordEyeScanTimer);
                        vm._discordEyeScanTimer = null;
                    }
                };
                vm._discordEyeSetPupils = function (host, cls, transform) {
                    if (!host || !host.querySelectorAll) return;
                    Array.prototype.forEach.call(host.querySelectorAll('.brand-pupil'), function (el) {
                        if (cls) {
                            el.classList.add(cls);
                        } else {
                            el.classList.remove('brand-eye-out');
                            el.classList.remove('brand-eye-back');
                        }
                        if (transform) el.setAttribute('transform', transform);
                        else el.removeAttribute('transform');
                    });
                };
                vm._scheduleDiscordEyeScan = function () {
                    vm._cancelDiscordEyeScan();
                    if (!vm.showDiscordPanel) return;
                    vm._discordEyeScanTimer = $timeout(vm._discordEyeScanMove, 2600 + Math.random() * 6400);
                };
                vm._discordEyeScanMove = function () {
                    vm._discordEyeScanTimer = null;
                    if (!vm.showDiscordPanel) return;
                    // Hovering? mouseleave will reschedule — don't fight the cursor.
                    if (vm._discordBrandHovered) return;
                    var host = document.querySelector('.discord-brand');
                    if (!host || !host.querySelectorAll || !host.querySelectorAll('.brand-pupil').length) return;
                    // Random action each beat: mostly a quick blink, sometimes a
                    // glance, sometimes a double-glance — all on unpredictable
                    // intervals so the spider never moves on a visible beat.
                    var roll = Math.random();
                    if (roll < 0.4) {
                        vm._discordEyeBlink(host, function () { vm._scheduleDiscordEyeScan(); });
                    } else if (roll < 0.55) {
                        var dir = Math.random() < 0.5 ? -1 : 1;
                        vm._discordEyeGlance(host, dir, function () {
                            // A blink right after a glance reads very naturally.
                            if (Math.random() < 0.35) {
                                vm._discordEyeBlink(host, function () { vm._scheduleDiscordEyeScan(); });
                            } else {
                                vm._scheduleDiscordEyeScan();
                            }
                        });
                    } else {
                        var d = Math.random() < 0.5 ? -1 : 1;
                        vm._discordEyeGlance(host, d, function () {
                            vm._discordEyeGlance(host, -d, function () { vm._scheduleDiscordEyeScan(); });
                        });
                    }
                };
                vm._discordEyeBlink = function (host, done) {
                    if (!host || !host.querySelectorAll) { done(); return; }
                    Array.prototype.forEach.call(host.querySelectorAll('.brand-lid'), function (el) {
                        el.classList.remove('brand-blink');
                        void el.getBoundingClientRect(); // restart cleanly on rapid blinks
                        el.classList.add('brand-blink');
                    });
                    vm._discordEyeScanTimer = $timeout(function () {
                        vm._discordEyeScanTimer = null;
                        Array.prototype.forEach.call(host.querySelectorAll('.brand-lid'), function (el) {
                            el.classList.remove('brand-blink');
                        });
                        done();
                    }, 330);
                };
                vm._discordEyeGlance = function (host, dir, done) {
                    vm._discordEyeSetPupils(host, 'brand-eye-out', 'translate(' + (0.8 * dir) + ' 0)');
                    vm._discordEyeScanTimer = $timeout(function () {
                        vm._discordEyeScanTimer = null;
                        if (!vm.showDiscordPanel || vm._discordBrandHovered) {
                            vm._discordEyeSetPupils(host, '', '');
                            done();
                            return;
                        }
                        // Drift back to centre on the slower easing.
                        vm._discordEyeSetPupils(host, 'brand-eye-back', '');
                        vm._discordEyeScanTimer = $timeout(done, 850 + Math.random() * 350);
                    }, 900 + Math.random() * 1500);
                };
                // Click: the big skulltula pounces — replay a quick squash-and-stretch.
                // Removing the class first (with a forced reflow) lets rapid re-clicks
                // restart the animation from scratch.
                vm.discordLogoClick = function (ev) {
                    var host = ev && ev.currentTarget;
                    var svg = host && host.querySelector && host.querySelector('.discord-brand-icon');
                    if (!svg) return;
                    if (svg.classList.contains('brand-strike')) {
                        svg.classList.remove('brand-strike');
                        void svg.getBoundingClientRect();
                    }
                    svg.classList.add('brand-strike');
                };
                // Subtle spider chitter: a short burst of bandpass-filtered noise
                // ticks synthesised with the Web Audio API (no asset file needed).
                vm._chitterCtx = null;
                vm._ensureChitterCtx = function () {
                    if (vm._chitterCtx) return vm._chitterCtx;
                    var AC = window.AudioContext || window.webkitAudioContext;
                    if (!AC) return null;
                    var ctx = new AC();
                    vm._chitterCtx = ctx;
                    if (ctx.state === 'suspended' && ctx.resume) { try { ctx.resume(); } catch (e) { /* noop */ } }
                    return ctx;
                };
                vm.discordLogoHover = function () {
                    var ctx = vm._ensureChitterCtx();
                    if (!ctx) return;
                    // Debounce: don't re-chitter on a quick leave/re-enter.
                    var nowMs = Date.now();
                    if (vm._lastChitterAt && nowMs - vm._lastChitterAt < 700) return;
                    vm._lastChitterAt = nowMs;
                    if (ctx.state === 'suspended' && ctx.resume) { try { ctx.resume(); } catch (e) { /* noop */ } }
                    var now = ctx.currentTime;
                    var master = ctx.createGain();
                    master.gain.value = 0.055; // quiet on purpose
                    master.connect(ctx.destination);
                    var ticks = 5 + Math.floor(Math.random() * 3);
                    var start = now + 0.02;
                    for (var i = 0; i < ticks; i++) {
                        (function (t0) {
                            var dur = 0.028 + Math.random() * 0.025;
                            var size = Math.max(1, Math.floor(ctx.sampleRate * dur));
                            var buf = ctx.createBuffer(1, size, ctx.sampleRate);
                            var d = buf.getChannelData(0);
                            for (var j = 0; j < size; j++) d[j] = Math.random() * 2 - 1;
                            var src = ctx.createBufferSource();
                            src.buffer = buf;
                            var bp = ctx.createBiquadFilter();
                            bp.type = 'bandpass';
                            bp.frequency.value = 2600 + Math.random() * 2400;
                            bp.Q.value = 3 + Math.random() * 3;
                            var g = ctx.createGain();
                            g.gain.setValueAtTime(0.0001, t0);
                            g.gain.exponentialRampToValueAtTime(1, t0 + 0.004);
                            g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
                            src.connect(bp); bp.connect(g); g.connect(master);
                            src.start(t0);
                            src.stop(t0 + dur + 0.01);
                        })(start + i * (0.055 + Math.random() * 0.05));
                    }
                };
                vm.showChangelog = false;
                vm.changelogContent = '';
                vm.changelogHtml = function () {
                    var text = vm.changelogContent || '';
                    var esc = String(text)
                        .replace(/&/g, '&amp;')
                        .replace(/</g, '&lt;')
                        .replace(/>/g, '&gt;')
                        .replace(/"/g, '&quot;');
                    return esc.replace(/(^|[\s([{])@([A-Za-z0-9][A-Za-z0-9_-]*)/g,
                        function (m, lead, user) {
                            return lead + '<a class="changelog-mention" href="https://github.com/' + encodeURIComponent(user) +
                                '" target="_blank" rel="noopener noreferrer" data-mention="' +
                                encodeURIComponent(user) + '">@' + user + '</a>';
                        });
                };
                vm.changelogSyncLabel = '';
                vm.changelogFontSize = parseInt(localStorage.getItem('weaverChangelogFontSize'), 10) || 78;
                vm.changelogFontSizeUp = function () { vm.changelogFontSize = Math.min(vm.changelogFontSize + 12, 160); localStorage.setItem('weaverChangelogFontSize', vm.changelogFontSize); };
                vm.changelogFontSizeDown = function () { vm.changelogFontSize = Math.max(vm.changelogFontSize - 12, 40); localStorage.setItem('weaverChangelogFontSize', vm.changelogFontSize); };
                vm.toggleChangelog = function () {
                    vm.showChangelog = !vm.showChangelog;
                    if (vm.showChangelog) vm.loadChangelog();
                };
                vm.loadChangelog = function () {
                    vm.changelogLoading = true;
                    vm.changelogContent = '';
                    vm.changelogSyncLabel = '';
                    $http.get('/api/changelog', { timeout: 15000 }).then(function (resp) {
                        vm.changelogContent = (resp.data && resp.data.content) || '';
                        var lastSynced = resp.data && resp.data.lastSynced;
                        if (lastSynced) {
                            try {
                                var d = new Date(lastSynced);
                                var diffMin = Math.floor((new Date() - d) / 60000);
                                var timeStr = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
                                var dateStr = d.toLocaleDateString([], { month: 'short', day: 'numeric', year: 'numeric' });
                                vm.changelogSyncLabel = diffMin < 1 ? 'Fetched just now' : diffMin < 60 ? 'Fetched ' + diffMin + 'm ago' : 'Fetched ' + dateStr + ' ' + timeStr;
                            } catch (e) {}
                        }
                        vm.changelogLoading = false;
                    }, function () { vm.changelogContent = ''; vm.changelogLoading = false; });
                };
                vm.loadVersion = function () { $http.get('/api/bughosted/version', { timeout: 10000 }).then(function (resp) { vm.appVersion = resp.data; }, function () { vm.appVersion = { local: '?', remote: null, updateAvailable: false }; }); };
                vm.triggerUpdate = function () {
                    vm.updating = true;
                    vm.updateProgress = { stage: 'starting', percent: 0, bytesDownloaded: 0, totalBytes: 0 };
                    $http.post('/api/bughosted/update').then(function () {
                        pollUpdateProgress();
                    }, function () {
                        vm.updating = false;
                        vm.updateProgress = null;
                        alert('Update failed.');
                    });
                };

                function pollUpdateProgress() {
                    var poll = function () {
                        $http.get('/api/bughosted/update-progress', { timeout: 3000 }).then(function (resp) {
                            vm.updateProgress = resp.data;
                            if (resp.data.stage === 'failed') {
                                vm.updating = false;
                                alert('Update failed.');
                            } else if (resp.data.stage === 'installing' || resp.data.stage === 'restarting') {
                                vm.updateProgress = { stage: 'restarting', percent: 100, bytesDownloaded: 0, totalBytes: 0 };
                                waitForServer();
                            } else {
                                $timeout(poll, 500);
                            }
                        }, function () {
                            vm.updateProgress = { stage: 'restarting', percent: 100, bytesDownloaded: 0, totalBytes: 0 };
                            waitForServer();
                        });
                    };
                    $timeout(poll, 1000);
                }

                function shutdownBackground() {
                    vm.shuttingDown = true;
                    if (vm.stopBughostedTimers) vm.stopBughostedTimers();
                    if (vm.pauseTerminalPolling) vm.pauseTerminalPolling();
                    if (vm.stopIdePolling) vm.stopIdePolling();
                }

                function waitForServer(fromManual) {
                    shutdownBackground();
                    var started = Date.now();
                    var retry = function () {
                        var elapsed = (Date.now() - started) / 1000;
                        if (elapsed > 15) vm.updateProgress.stuck = true;
                        if (fromManual && elapsed > 60) { vm.updateProgress.stuck = true; return; }
                        $http.get('/api/bughosted/version', { timeout: 5000 }).then(function () {
                            window.location.reload();
                        }, function () {
                            $timeout(retry, fromManual ? 500 : 2000);
                        });
                    };
                    $timeout(retry, fromManual ? 500 : 1000);
                }
                vm.reloadNow = function () { waitForServer(true); };

                // The selected project's live DTO in vm.projects (path-normalized match), or
                // null — the Settings panel's suggestion controls edit THIS project's per-project
                // settings (SuggestionContextDepth / IdleSuggestions / MaxSuggestionsPerCard).
                vm.selectedProjectSuggestionDto = function () {
                    if (!vm.selectedProject || !Array.isArray(vm.projects)) return null;
                    var norm = function (s) { return String(s || '').replace(/\\/g, '/').replace(/\/+$/g, '').toLowerCase(); };
                    var target = norm(vm.selectedProject);
                    for (var i = 0; i < vm.projects.length; i++) {
                        var p = vm.projects[i];
                        if (p && norm(p.Path || p.path) === target) return p;
                    }
                    return null;
                };
                vm.refreshSuggestionSettings = function () {
                    var p = vm.selectedProjectSuggestionDto();
                    vm.settingsSuggestionContextDepth = p ? (p.SuggestionContextDepth || 'full') : 'full';
                    vm.settingsIdleSuggestions = p ? p.IdleSuggestions !== false : true;
                    var ms = p ? p.MaxSuggestionsPerCard : undefined;
                    vm.settingsMaxSuggestionsPerCard = (typeof ms === 'number' && ms >= 0 && ms <= 4) ? Math.round(ms) : 3;
                };

                vm.openSettingsPanel = function () {
                    vm.settingsDefaultProject = vm.defaultProject || vm.selectedProject; vm.showSettingsPanel = true;
                    vm.refreshSuggestionSettings();
                    vm.loadIdeThemeStylesheets();
                    $timeout(function () { vm.renderThemePreviews(); }, 150);
                    vm.fileHintsData = (vm.projects || []).map(function (p) { return { projectPath: p.Path, hints: [] }; });
                    $http.get('/api/filehints').then(function (resp) {
                        try {
                            var store = typeof resp.data === 'string' ? JSON.parse(resp.data) : resp.data;
                            if (store && store.Projects) {
                                vm.projects.forEach(function (p, i) {
                                    var proj = store.Projects[p.Path];
                                    vm.fileHintsData[i] = { projectPath: p.Path, hints: proj && proj.Hints ? proj.Hints.map(function (h) { return { keywords: (h.Keywords || []).join(', '), files: (h.Files || []).length > 0 ? h.Files.slice() : [''] }; }) : [] };
                                });
                            }
                        } catch (e) { }
                    });
                    var backdrop = document.getElementById('backdrop'); if (backdrop) backdrop.style.display = 'block';
                };
                var _themeSaveDebounce = null;
                vm.applyThemeColors = function (el, colors) {
                    applyTheme(el, colors);
                    if (_themeSaveDebounce) $timeout.cancel(_themeSaveDebounce);
                    _themeSaveDebounce = $timeout(function () {
                        vm.saveSettings(true);
                        _themeSaveDebounce = null;
                    }, 800, false);
                };
                vm.resetThemeColors = function () {
                    vm.themeColors = {};
                    Object.keys(DEFAULT_THEME).forEach(function (k) { vm.themeColors[k] = DEFAULT_THEME[k]; });
                    applyTheme(null, vm.themeColors);
                    vm.saveSettings(true);
                };
                vm.applyPresetTheme = function (name) {
                    var preset = PRESET_THEMES[name];
                    if (!preset) return;
                    var diff = vm.computeThemeDiff(vm.themeColors, preset);
                    Object.keys(preset).forEach(function (k) { vm.themeColors[k] = preset[k]; });
                    applyTheme(null, vm.themeColors);
                    vm.saveSettings(true);
                    vm.showThemeDiff('Applied preset “' + name + '”', diff);
                };

                // ── Theme change diff ────────────────────────────────────────────────
                // Shows exactly which color variables a preset/import will alter.
                vm.themeDiff = null; // { title, rows: [{key, from, to}] }
                vm.computeThemeDiff = function (before, after) {
                    var rows = [];
                    var keys = {};
                    Object.keys(before || {}).forEach(function (k) { keys[k] = 1; });
                    Object.keys(after || {}).forEach(function (k) { keys[k] = 1; });
                    Object.keys(keys).sort().forEach(function (k) {
                        var a = (before && before[k]) || '';
                        var b = (after && after[k]) || '';
                        if (a.toLowerCase() !== b.toLowerCase()) {
                            rows.push({ key: k, from: a, to: b });
                        }
                    });
                    return rows;
                };
                vm.showThemeDiff = function (title, rows) {
                    vm.themeDiff = { title: title, rows: rows };
                };
                vm.clearThemeDiff = function () { vm.themeDiff = null; };
                // Live preview as the user types/pastes JSON: parse (without applying),
                // then show what would change so they can commit deliberately. Rendered
                // right under the paste box (vm.themeImportPreview), separate from the
                // post-apply diff panel (vm.themeDiff) shown near the presets.
                vm.themeImportPreview = null;
                vm.previewPastedThemeDiff = function () {
                    var text = vm.themeImportText || '';
                    if (!text.trim()) { vm.themeImportPreview = null; return; }
                    var parsed = null;
                    try { parsed = JSON.parse(text); } catch (e) { vm.themeImportPreview = null; return; }
                    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) { vm.themeImportPreview = null; return; }
                    // Same source-key validation as importThemeJson so the preview only
                    // shows changes for JSON that can actually be applied.
                    var sourceKeys = validThemeSourceKeys(parsed);
                    if (sourceKeys.length === 0) {
                        vm.themeImportPreview = { invalid: true, rows: [] };
                        return;
                    }
                    var merged = mergeTheme(parsed);
                    var rows = vm.computeThemeDiff(vm.themeColors, merged);
                    vm.themeImportPreview = { invalid: false, rows: rows };
                };

                // ── Named theme presets ───────────────────────────────────────────
                vm.themeEquals = function (a, b) {
                    return Object.keys(DEFAULT_THEME).every(function (k) {
                        return (a[k] || '').toLowerCase() === (b[k] || '').toLowerCase();
                    });
                };
                vm.isPresetActive = function (name) {
                    var preset = PRESET_THEMES[name];
                    return preset ? vm.themeEquals(vm.themeColors, preset) : false;
                };
                vm.isSavedThemeActive = function (t) {
                    return t && t.colors ? vm.themeEquals(vm.themeColors, t.colors) : false;
                };
                vm.themeSwatches = function (colors) {
                    return [colors['--bg'], colors['--surface'], colors['--accent'], colors['--success'], colors['--error']];
                };
                vm.presetSwatches = function (name) {
                    var p = PRESET_THEMES[name];
                    return p ? vm.themeSwatches(p) : [];
                };
                vm.saveCurrentTheme = function () {
                    var name = (vm.newThemeName || '').trim();
                    if (!name) { $window.alert('Enter a name for this theme first.'); return; }
                    var existing = vm.savedThemes.find(function (t) { return t.name.toLowerCase() === name.toLowerCase(); });
                    if (existing) {
                        if (!$window.confirm('A theme named "' + name + '" already exists. Replace it?')) return;
                        existing.colors = angular.copy(vm.themeColors);
                    } else {
                        vm.savedThemes.push({ name: name, colors: angular.copy(vm.themeColors), _editing: false, _editName: '' });
                    }
                    vm.newThemeName = '';
                    vm.saveSettings(true);
                };
                vm.applySavedTheme = function (t) {
                    if (!t || !t.colors) return;
                    var diff = vm.computeThemeDiff(vm.themeColors, t.colors);
                    Object.keys(t.colors).forEach(function (k) { vm.themeColors[k] = t.colors[k]; });
                    applyTheme(null, vm.themeColors);
                    vm.saveSettings(true);
                    vm.showThemeDiff('Applied saved theme “' + t.name + '”', diff);
                };
                vm.startRenameTheme = function (t) {
                    t._editing = true;
                    t._editName = t.name;
                };
                vm.commitRenameTheme = function (t) {
                    var name = (t._editName || '').trim();
                    if (name && name.toLowerCase() !== t.name.toLowerCase()) {
                        var clash = vm.savedThemes.find(function (x) { return x !== t && x.name.toLowerCase() === name.toLowerCase(); });
                        if (clash) {
                            $window.alert('Another theme is already named "' + name + '".');
                            t._editName = t.name; // revert input
                            t._editing = false;   // exit edit mode so blur stops re-alerting
                            return;
                        }
                        t.name = name;
                        vm.saveSettings(true);
                    }
                    t._editing = false;
                };
                vm.cancelRenameTheme = function (t) {
                    t._editName = t.name;
                    t._editing = false;
                };
                vm.deleteSavedTheme = function (t) {
                    if (!t) return;
                    if (!$window.confirm('Delete theme "' + t.name + '"? Your current colors are not changed.')) return;
                    vm.savedThemes = vm.savedThemes.filter(function (x) { return x !== t; });
                    vm.saveSettings(true);
                };
                // ── Theme export / import (share JSON between machines) ───────────
                vm._copyFallback = function (text) {
                    var ta = document.createElement('textarea');
                    ta.value = text;
                    ta.style.position = 'fixed';
                    ta.style.opacity = '0';
                    document.body.appendChild(ta);
                    ta.select();
                    try { document.execCommand('copy'); } catch (e) { }
                    document.body.removeChild(ta);
                };
                vm.importThemeJson = function (json) {
                    if (!json || !json.trim()) return;
                    var parsed = null;
                    try {
                        parsed = JSON.parse(json);
                    } catch (e) {
                        vm.themeTransferMsg = '✗ Invalid JSON — could not import';
                        $timeout(function () { vm.themeTransferMsg = ''; }, 3000);
                        return;
                    }
                    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
                        vm.themeTransferMsg = '✗ Expected a JSON object of colors';
                        $timeout(function () { vm.themeTransferMsg = ''; }, 3000);
                        return;
                    }
                    // Validate the SOURCE keys before merging — mergeTheme backfills defaults,
                    // so checking merged alone would always pass.
                    var sourceKeys = validThemeSourceKeys(parsed);
                    if (sourceKeys.length === 0) {
                        vm.themeTransferMsg = '✗ No valid color variables found (expected "--bg": "#071025")';
                        $timeout(function () { vm.themeTransferMsg = ''; }, 3500);
                        return;
                    }
                    var merged = mergeTheme(parsed);
                    var before = vm.themeColors;
                    vm.themeColors = merged;
                    applyTheme(null, vm.themeColors);
                    vm.saveSettings(true);
                    vm.themeImportText = '';
                    vm.themeImportPreview = null;
                    vm.showThemeDiff('Imported theme', vm.computeThemeDiff(before, merged));
                    vm.themeTransferMsg = '✓ Theme imported (' + sourceKeys.length + ' colors)';
                    $timeout(function () { vm.themeTransferMsg = ''; }, 2500);
                };
                vm.importThemeFromClipboard = function () {
                    if (navigator.clipboard && navigator.clipboard.readText) {
                        navigator.clipboard.readText().then(function (text) {
                            vm.importThemeJson(text);
                        }, function () {
                            vm.themeTransferMsg = '⚠ Clipboard blocked — paste the JSON below instead';
                            $timeout(function () { vm.themeTransferMsg = ''; }, 3000);
                        });
                    } else {
                        vm.themeTransferMsg = '⚠ Clipboard API unavailable — paste the JSON below instead';
                        $timeout(function () { vm.themeTransferMsg = ''; }, 3000);
                    }
                };
                // ── Drag & drop a .json theme file onto the JSON preview box ──────
                // Counter-based so dragging over the box's children doesn't flicker
                // the highlight. Reading happens via FileReader; importThemeJson does
                // the parse/validate/apply/save.
                vm.themeDragOver = false;
                vm._themeDragDepth = 0;
                vm.onThemeDragEnter = function (e) {
                    if (e && e.preventDefault) e.preventDefault();
                    vm._themeDragDepth++;
                    vm.themeDragOver = true;
                };
                vm.onThemeDragOver = function (e) {
                    if (e && e.preventDefault) e.preventDefault();
                    if (e && e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
                };
                vm.onThemeDragLeave = function () {
                    vm._themeDragDepth = Math.max(0, vm._themeDragDepth - 1);
                    if (vm._themeDragDepth === 0) vm.themeDragOver = false;
                };
                vm.onThemeDrop = function (e) {
                    vm._themeDragDepth = 0;
                    vm.themeDragOver = false;
                    if (!e) return;
                    if (e.preventDefault) e.preventDefault();
                    if (e.stopPropagation) e.stopPropagation();
                    var file = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
                    if (!file) {
                        vm.themeTransferMsg = '⚠ No file dropped — paste the JSON below instead';
                        $timeout(function () { vm.themeTransferMsg = ''; }, 3000);
                        return;
                    }
                    if (!/\.json$/i.test(file.name || '')) {
                        vm.themeTransferMsg = '✗ "' + file.name + '" is not a .json file — dropping ignored';
                        $timeout(function () { vm.themeTransferMsg = ''; }, 3500);
                        return;
                    }
                    if (file.size > 2 * 1024 * 1024) {
                        vm.themeTransferMsg = '✗ "' + file.name + '" is over 2 MB — too large for a theme file';
                        $timeout(function () { vm.themeTransferMsg = ''; }, 3500);
                        return;
                    }
                    var reader = new FileReader();
                    reader.onload = function (ev) {
                        vm.importThemeJson(String(ev.target.result || ''));
                    };
                    reader.onerror = function () {
                        vm.themeTransferMsg = '✗ Could not read "' + file.name + '" — try pasting the JSON below instead';
                        $timeout(function () { vm.themeTransferMsg = ''; }, 3500);
                    };
                    reader.readAsText(file);
                };

                vm.applyIdeTheme = function (name) {
                    if (!name) name = 'weaver-dark';
                    vm.ideTheme = name;
                    var linkId = 'cm-ide-theme';
                    var existing = document.getElementById(linkId);
                    if (existing) existing.parentNode.removeChild(existing);
                    if (name !== 'weaver-dark') {
                        var link = document.createElement('link');
                        link.id = linkId;
                        link.rel = 'stylesheet';
                        link.href = 'https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.17/theme/' + name + '.min.css';
                        document.head.appendChild(link);
                    }
                    if (vm._editor) {
                        vm._editor.setOption('theme', name);
                    }
                    vm.saveSettings(true);
                };

                var _previewCssLoaded = false;
                var _previewSample =
                    'const fib = (n) => {\n' +
                    '  if (n <= 1) return n; // base case\n' +
                    '  return fib(n - 1) + fib(n - 2);\n' +
                    '};\n' +
                    'console.log(fib(10));\n';

                vm.loadIdeThemeStylesheets = function () {
                    if (_previewCssLoaded || !vm.ideThemeList) return;
                    _previewCssLoaded = true;
                    vm.ideThemeList.forEach(function (t) {
                        if (t.value === 'weaver-dark') return;
                        var id = 'cm-theme-css-' + t.value;
                        if (document.getElementById(id)) return;
                        var link = document.createElement('link');
                        link.id = id;
                        link.rel = 'stylesheet';
                        link.href = 'https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.17/theme/' + t.value + '.min.css';
                        link.onload = function () { vm.renderThemePreviews(); };
                        document.head.appendChild(link);
                    });
                };

                vm.renderThemePreviews = function () {
                    if (!window.CodeMirror) return;
                    var nodes = document.querySelectorAll('.theme-card-preview[data-theme]');
                    for (var i = 0; i < nodes.length; i++) {
                        (function (el) {
                            var theme = el.getAttribute('data-theme');
                            if (!theme) return;
                            if (el._cmPreview) {
                                if (el._cmPreview.getOption('theme') !== theme) el._cmPreview.setOption('theme', theme);
                                el._cmPreview.refresh();
                                return;
                            }
                            el._cmPreview = CodeMirror(el, {
                                value: _previewSample,
                                mode: 'javascript',
                                theme: theme,
                                readOnly: true,
                                cursorBlinkRate: -1,
                                lineNumbers: false,
                                foldGutter: false,
                                gutters: [],
                                indentUnit: 2,
                                tabSize: 2
                            });
                            el._cmPreview.refresh();
                        })(nodes[i]);
                    }
                };

                vm.destroyThemePreviews = function () {
                    var nodes = document.querySelectorAll('.theme-card-preview[data-theme]');
                    for (var i = 0; i < nodes.length; i++) {
                        var el = nodes[i];
                        if (el._cmPreview) {
                            var wrapper = el._cmPreview.getWrapperElement();
                            if (wrapper && wrapper.parentNode) wrapper.parentNode.removeChild(wrapper);
                            el._cmPreview = null;
                        }
                        el.innerHTML = '';
                    }
                };

                $scope.$watch(function () { return vm.ideThemeFilter + '|' + vm.settingsTab; }, function () {
                    if (vm.showSettingsPanel) {
                        $timeout(function () { vm.renderThemePreviews(); }, 0);
                    }
                });
                vm.closeSettingsPanel = function (event) {
                    if (_themeSaveDebounce) { $timeout.cancel(_themeSaveDebounce); _themeSaveDebounce = null; }
                    if (event && event.target.tagName === 'INPUT') return;
                    if (event) event.stopPropagation();
                    vm.showSettingsPanel = false;
                    vm.destroyThemePreviews();
                    var backdrop = document.getElementById('backdrop'); if (backdrop) backdrop.style.display = 'none';
                };

                $http.get('/api/filehints').then(function (resp) {
                    try { var store = typeof resp.data === 'string' ? JSON.parse(resp.data) : resp.data; if (store && store.Projects) vm._preloadedFileHints = store.Projects; } catch (e) { }
                });
            }
        };
    }]);

// The app doesn't load ngSanitize, so ng-bind-html is unavailable. This tiny
// directive renders an already-escaped-and-linked HTML string into an element
// instead (used by the "What's new" changelog to turn @usernames into links).
// It also wires up a room-wide hover preview for those @mentions (GitHub avatar
// + repo info) so the tooltip can appear anywhere on screen, even past the
// changelog panel's scroll overflow.
angular.module('kanbanApp').directive('appSafeHtml', function () {
    return {
        restrict: 'A',
        link: function (scope, element, attrs) {
            var el = element[0];
            var tip = null;
            var tipInfo = null;
            var tipUser = null;
            var hoverTimer = null;
            var repoCache = {};

            function ensureTip() {
                if (tip) return;
                tip = document.createElement('div');
                tip.className = 'mention-tooltip';
                tipInfo = document.createElement('div');
                tipInfo.className = 'mention-tooltip-info';
                tip.appendChild(tipInfo);
                document.body.appendChild(tip);
            }

            function positionTip(anchor) {
                var r = anchor.getBoundingClientRect();
                var tw = tip.offsetWidth || 260;
                var th = tip.offsetHeight || 90;
                var left = r.left + r.width / 2 - tw / 2;
                left = Math.max(8, Math.min(left, window.innerWidth - tw - 8));
                var top = r.top - th - 12;
                if (top < 8) top = r.bottom + 12; // flip below if no room above
                tip.style.left = left + 'px';
                tip.style.top = top + 'px';
                tip.setAttribute('data-edge', top >= r.bottom ? 'bottom' : 'top');
            }

            function avatarUrl(user) {
                return 'https://github.com/' + encodeURIComponent(user) + '.png?size=160';
            }

            function showTip(anchor, user) {
                ensureTip();
                tipUser = user;
                tipInfo.innerHTML =
                    '<div class="mention-tooltip-row">' +
                        '<img class="mention-tooltip-avatar" src="' + avatarUrl(user) +
                        '" alt="" onerror="this.style.visibility=\'hidden\'">' +
                        '<div class="mention-tooltip-id">' +
                            '<span class="mention-tooltip-name">@' + user + '</span>' +
                            '<span class="mention-tooltip-sub">Loading GitHub info…</span>' +
                        '</div>' +
                    '</div>' +
                    '<div class="mention-tooltip-repos"></div>';
                tip.style.display = 'block';
                positionTip(anchor);

                var doLoad = function () {
                    if (repoCache[user]) {
                        tipInfo.querySelector('.mention-tooltip-sub').textContent =
                            repoCache[user][1];
                        if (repoCache[user][2]) {
                            tipInfo.querySelector('.mention-tooltip-name').textContent =
                                repoCache[user][2] + ' (@' + user + ')';
                        }
                        return;
                    }
                    // Unauthenticated GitHub api is CORS-open; cache per user in-session.
                    var repos = fetch('https://api.github.com/users/' + encodeURIComponent(user) +
                        '/repos?sort=updated&per_page=3').then(function (r) { return r.json(); });
                    var profile = fetch('https://api.github.com/users/' + encodeURIComponent(user))
                        .then(function (r) { return r.json(); });
                    Promise.allSettled([repos, profile]).then(function (results) {
                        var name = '';
                        var list = [];
                        var meta = '';
                        if (results[1].status === 'fulfilled') {
                            var p = results[1].value;
                            name = p.name || '';
                            meta = (p.public_repos != null ? p.public_repos + ' public repo' +
                                (p.public_repos === 1 ? '' : 's') : '') +
                                (p.followers != null ? ' · ' + p.followers + ' followers' : '');
                        }
                        if (results[0].status === 'fulfilled' && Array.isArray(results[0].value)) {
                            list = results[0].value.slice(0, 3).map(function (r) {
                                return { name: r.name, desc: r.description || '', lang: r.language || '' };
                            });
                        }
                        var sub;
                        if (list.length) {
                            sub = 'Repos: ' + list.map(function (r) { return r.name; }).join(', ');
                        } else {
                            sub = meta || 'On GitHub @' + user;
                        }
                        repoCache[user] = [list, sub, name];
                        if (tipUser === user) {
                            var nameEl = tipInfo.querySelector('.mention-tooltip-name');
                            if (name && nameEl) nameEl.textContent = name + ' (@' + user + ')';
                            var subEl = tipInfo.querySelector('.mention-tooltip-sub');
                            if (subEl) subEl.textContent = sub;
                            var listEl = tipInfo.querySelector('.mention-tooltip-repos');
                            if (listEl && list.length) {
                                listEl.innerHTML = '';
                                list.forEach(function (r) {
                                    var li = document.createElement('div');
                                    li.className = 'mention-tooltip-repo';
                                    li.innerHTML = '<span class="mention-tooltip-repo-name">' +
                                        escapeHtml(r.name) + '</span>' +
                                        (r.lang ? ' <span class="mention-tooltip-lang">' + escapeHtml(r.lang) + '</span>' : '') +
                                        (r.desc ? '<div class="mention-tooltip-repo-desc">' + escapeHtml(r.desc) + '</div>' : '');
                                    listEl.appendChild(li);
                                });
                            }
                        }
                    }).catch(function () {
                        repoCache[user] = [[], 'On GitHub @' + user, ''];
                    });
                };
                doLoad();
            }

            function escapeHtml(s) {
                return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;')
                    .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
            }

            function hideTip() {
                clearTimeout(hoverTimer);
                if (tip) tip.style.display = 'none';
                tipUser = null;
            }

            function onMouseOver(e) {
                var t = e.target;
                var anchor = (t.closest && t.closest('.changelog-mention')) ||
                    (t.parentNode && t.parentNode.closest && t.parentNode.closest('.changelog-mention'));
                if (!anchor) return;
                var user = (anchor.getAttribute('data-mention') || '').replace(/@/g, '');
                if (!user) return;
                clearTimeout(hoverTimer);
                hoverTimer = setTimeout(function () {
                    showTip(anchor, user);
                }, 120);
            }

            function onMouseOut(e) {
                var t = e.target;
                var anchor = (t.closest && t.closest('.changelog-mention')) ||
                    (t.parentNode && t.parentNode.closest && t.parentNode.closest('.changelog-mention'));
                if (!anchor) return;
                var related = e.relatedTarget || (e.toElement !== null ? e.toElement : null);
                if (tip && related && tip.contains(related)) return; // staying inside tooltip
                hideTip();
            }

            function onScroll() { hideTip(); }

            el.addEventListener('mouseover', onMouseOver);
            el.addEventListener('mouseout', onMouseOut);
            window.addEventListener('scroll', onScroll, true);
            element.on('$destroy', function () {
                window.removeEventListener('scroll', onScroll, true);
                if (tip && tip.parentNode) tip.parentNode.removeChild(tip);
            });

            scope.$watch(attrs.appSafeHtml, function (html) {
                el.innerHTML = html || '';
            });
        }
    };
});
