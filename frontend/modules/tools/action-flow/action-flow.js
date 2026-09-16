(function () {
'use strict';

const BLOCKLY_CDN     = 'https://unpkg.com/blockly@10.4.3/blockly.min.js';
const TICK_INTERVAL_MS = 30 * 1000;
const LOG_MAX_ENTRIES  = 60;
const AUTOSAVE_DEBOUNCE_MS = 600;

const COLOR_LOGIC   = '#7784ed';
const COLOR_TIME    = '#7dd1ff';
const COLOR_PARAM   = '#cb71ff';
const COLOR_ACTION  = '#937dff';
const COLOR_TRIGGER = '#c27dff';
const COLOR_GAME    = '#2c4e8a';
const COLOR_OTHER   = '#43c59e';
const COLOR_INFO    = '#f0a14e';
const COLOR_VRCN    = '#4ec9f0';
const COLOR_FRIEND  = '#c9702a';

const WORLD_CHANGE_DELAY_MS = 15 * 1000;
const EVENT_TICK_MS = 5 * 1000;
const FLOW_ACTION_LIMIT = 20;
const FLOW_LIMIT = 4;
const TRIGGER_LIMIT = 16;
const TASK_WINDOW_MS = 10 * 60 * 1000;
const TASK_SOFT_LIMIT = 20;
const TASK_HARD_LIMIT = 25;
const TASK_EXEMPT_TYPES = new Set([
    'af_send_notification',
    'af_send_notification_value',
    'af_send_advanced_notification',
    'af_send_own_instance_info',
    'af_send_own_advanced_instance_info',
    'af_send_friend_instance_info',
    'af_send_webhook',
    'af_send_webhook_value',
    'af_send_advanced_webhook',
    'af_set_feature',
    'af_osc_set_bool',
    'af_osc_set_float',
    'af_osc_set_int',
    'af_close_vrchat',
]);
const ACTION_TYPES = new Set([
    'af_set_status',
    'af_set_bio_text',
    'af_invite_friend',
    'af_request_invite',
    'af_answer_invite',
    'af_answer_invite_request',
    'af_send_notification',
    'af_send_notification_value',
    'af_send_advanced_notification',
    'af_switch_own_avatar',
    'af_switch_favorite_avatar',
    'af_switch_avatar_id',
    'af_set_home_world',
    'af_close_vrchat',
    'af_send_own_instance_info',
    'af_send_own_advanced_instance_info',
    'af_send_friend_instance_info',
    'af_send_webhook',
    'af_send_webhook_value',
    'af_send_advanced_webhook',
    'af_set_feature',
    'af_osc_set_bool',
    'af_osc_set_float',
    'af_osc_set_int',
]);

const aft  = (k, f) => (typeof t  === 'function' ? t ('action_flow.' + k, f)        : f);
const AF_FEATURES = [
    ['vr_overlay',       'VR Overlay',       () => typeof vroConnected   !== 'undefined' && vroConnected,
        () => vroConnect(), () => vroConnect()],
    ['kikitan_xd',       'KikitanXD',        () => typeof kxdRunning     !== 'undefined' && kxdRunning,
        () => kxdConnect(), () => kxdConnect()],
    ['youtube_fix',      'YouTube Fix',      () => !!document.getElementById('vcDot')?.classList.contains('online'),
        () => sendToCS({ action: 'vcStart' }), () => sendToCS({ action: 'vcStop' })],
    ['discord_presence', 'Discord Presence', () => typeof _dpRunning     !== 'undefined' && _dpRunning,
        () => sendToCS({ action: 'dpStart' }), () => sendToCS({ action: 'dpStop' })],
    ['voice_fight',      'Voice Fight',      () => typeof vfRunning      !== 'undefined' && vfRunning,
        () => { if (typeof vfOnTabOpen === 'function') vfOnTabOpen(); vfConnect(); },
        () => sendToCS({ action: 'vfStop' })],
    ['frame_shot',       'FrameShot',        () => typeof fsConnected    !== 'undefined' && fsConnected,
        () => fsConnect(), () => sendToCS({ action: 'fsDisconnect' })],
    ['space_flight',     'Space Flight',     () => typeof sfConnected    !== 'undefined' && sfConnected,
        () => sfConnect(), () => sendToCS({ action: 'sfDisconnect' })],
    ['space_turn',       'Space Turn',       () => typeof stConnected    !== 'undefined' && stConnected,
        () => stConnect(), () => sendToCS({ action: 'stDisconnect' })],
    ['custom_chatbox',   'Custom Chatbox',   () => typeof chatboxEnabled !== 'undefined' && chatboxEnabled,
        () => toggleChatbox(), () => toggleChatbox()],
    ['media_relay',      'Media Relay',      () => typeof relayOn        !== 'undefined' && relayOn,
        () => sendToCS({ action: 'startRelay' }), () => sendToCS({ action: 'stopRelay' })],
    ['status_schedule',  'Status Schedule',  () => typeof ssEnabledState !== 'undefined' && ssEnabledState,
        () => ssSetEnabled(true), () => ssSetEnabled(false)],
    ['event_snipe',      'Event Snipe',      () => typeof _snipeRunning  !== 'undefined' && _snipeRunning,
        () => snipeToggle(), () => sendToCS({ action: 'vrcStopSnipe' })],
];
const afFeatureLabel = (id, fallback) => aft('feature.' + id, fallback);
const OSC_PICK_PLACEHOLDER = '';
function oscParamPicker(wantType) {
    return () => {
        const head = [[aft('osc.pick', '(pick parameter)'), OSC_PICK_PLACEHOLDER]];
        try {
            if (typeof oscParams !== 'undefined' && oscParams) {
                const names = Object.keys(oscParams)
                    .filter(n => !wantType || (oscParams[n] || {}).type === wantType)
                    .sort((a, b) => a.localeCompare(b));
                if (names.length) return head.concat(names.map(n => [n, n]));
            }
        } catch {}
        return head.concat([[aft('osc.no_params', '(connect the OSC Tool, or type the parameter name yourself)'), OSC_PICK_PLACEHOLDER]]);
    };
}
function oscPickValidator(newValue) {
    if (!newValue) return OSC_PICK_PLACEHOLDER;
    const src = this.getSourceBlock && this.getSourceBlock();
    if (src) setTimeout(() => { try { src.setFieldValue(newValue, 'PARAM'); } catch {} }, 0);
    return OSC_PICK_PLACEHOLDER;
}
const FEATURE_DROPDOWN_FACTORY = () => AF_FEATURES.map(x => [afFeatureLabel(x[0], x[1]), x[0]]);
const aftf = (k, v, f) => (typeof tf === 'function' ? tf('action_flow.' + k, v || {}, f) : f);
const STATUS_DROPDOWN_FACTORY = () => [
    [aft('status.online',         'Online'),         'active'],
    [aft('status.ask_me',         'Ask Me'),         'ask me'],
    [aft('status.do_not_disturb', 'Do Not Disturb'), 'busy'],
    [aft('status.join_me',        'Join Me'),        'join me'],
];
const VRC_STATUS_LABEL_KEYS = { active: 'status.online', 'ask me': 'status.ask_me', busy: 'status.do_not_disturb', 'join me': 'status.join_me' };
const vrcStatusLabel = (s) => aft(VRC_STATUS_LABEL_KEYS[s] || 'status.online', s);
const AMPM_DROPDOWN_FACTORY = () => [
    [aft('block.ampm_24h', '24h'), '24'],
    [aft('block.ampm_am',  'AM'),  'AM'],
    [aft('block.ampm_pm',  'PM'),  'PM'],
];

let afFlows           = [];
let afCurrentFlowId   = null;
let afWorkspace       = null;
let afBlocklyLoading  = false;
let afBlocklyLoaded   = false;
let afTickTimer       = null;
let afLogEntries      = [];
let afTabInitialized  = false;
let afAutoSaveTimer   = null;
let afAutoSaveSuppressed = false;

let afTriggerState = {};
let afWatchState = {
    lastInstanceUserIds: null,
    lastInstanceUsers:   null,
    lastStatus:          null,
};
let afTaskHistory = [];   /* timestamps (ms) of dispatched API-calling actions, last TASK_WINDOW_MS only */
let afConditions = {};         /* name -> current boolean value, persisted via backend */
let afVrcGameRunning = false;  /* mirrored from backend every tick via afGetGameRunning IPC */
let afConditionsSaveTimer = null;
let afContext = { triggeringUser: null, triggerKind: null, notificationId: null };

function afEnsureBlockly() {
    if (afBlocklyLoaded) return Promise.resolve();
    if (afBlocklyLoading) return afBlocklyLoading;
    afBlocklyLoading = new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src    = BLOCKLY_CDN;
        s.async  = true;
        s.onload = () => { afBlocklyLoaded = true; resolve(); };
        s.onerror = () => reject(new Error('Failed to load Blockly from CDN'));
        document.head.appendChild(s);
    });
    return afBlocklyLoading;
}

function afDefineBlocks() {
    const B = window.Blockly;

    const DO = aft('block.do', 'do');
    const friendDropdown = () => {
        try {
            if (typeof vrcFriendsData !== 'undefined' && Array.isArray(vrcFriendsData) && vrcFriendsData.length) {
                return vrcFriendsData
                    .slice()
                    .sort((a, b) => (a.displayName || '').localeCompare(b.displayName || ''))
                    .map(f => [f.displayName || f.id, f.id]);
            }
        } catch {}
        return [[aft('block.no_friends', '(no friends loaded)'), '']];
    };
    const friendDropdownAny = () => [[aft('block.any_friend', '(any friend)'), '']].concat(friendDropdown());
    const friendDropdownTrigger = () => [[aft('block.trigger_friend', '(triggering friend)'), '']].concat(friendDropdown());
    const favFriendGroupDropdown = () => {
        try {
            if (typeof favFriendGroups !== 'undefined' && Array.isArray(favFriendGroups) && favFriendGroups.length) {
                return favFriendGroups.map(g => {
                    const local = (typeof isLocalFavGroup === 'function') && isLocalFavGroup(g);
                    const name  = g.displayName || g.name;
                    return [local ? name + ' ' + aft('block.local_suffix', '(local)') : name, g.name];
                });
            }
        } catch {}
        return [[aft('block.no_fav_groups', '(no favorite groups loaded)'), '']];
    };

    function makeTriggerHat(typeName, labelFn) {
        B.Blocks[typeName] = { init() {
            labelFn(this);
            this.appendStatementInput('DO').appendField(DO);
            this.setColour(COLOR_TRIGGER);
            this.hat = 'cap';
        } };
    }

    function makeUserPresenceTriggerHat(typeName, headLabel) {
        B.Blocks[typeName] = {
            init() {
                this.appendDummyInput('HEADER')
                    .appendField(headLabel)
                    .appendField(new B.FieldCheckbox('FALSE', this._onFilterChange.bind(this)), 'FILTER')
                    .appendField(aft('trigger.filter_label', 'only specific user'));
                this.appendStatementInput('DO').appendField(DO);
                this.setColour(COLOR_TRIGGER);
                this.hat = 'cap';
            },
            _onFilterChange(newVal) {
                const enabled = (newVal === 'TRUE' || newVal === true);
                setTimeout(() => this._setFilterInput(enabled), 0);
                return newVal;
            },
            _setFilterInput(enabled) {
                if (enabled && !this.getInput('USERID_INPUT')) {
                    this.appendDummyInput('USERID_INPUT')
                        .appendField(aft('trigger.user_id_label', 'user id'))
                        .appendField(new B.FieldTextInput(''), 'USER_ID');
                    this.moveInputBefore('USERID_INPUT', 'DO');
                } else if (!enabled && this.getInput('USERID_INPUT')) {
                    this.removeInput('USERID_INPUT');
                }
            },
            saveExtraState() {
                return { filterEnabled: this.getFieldValue('FILTER') === 'TRUE' };
            },
            loadExtraState(state) {
                this._setFilterInput(!!(state && state.filterEnabled));
            },
        };
    }

    B.Blocks['af_trigger_every_second'] = { init() {
        this.appendDummyInput().appendField(aft('trigger.every_second', 'do every second'));
        this.appendStatementInput('DO').appendField(DO);
        this.setColour(COLOR_VRCN);
        this.hat = 'cap';
        this.setTooltip(aft('trigger.every_second_tooltip', 'Runs once per second. Works only with VRCN Actions and Logic blocks, does not work with VRC Actions or Webhook Actions (those are skipped).'));
    } };
    makeTriggerHat('af_trigger_interval_30s',         b => b.appendDummyInput().appendField(aft('trigger.every_30s', 'every 30 seconds')));
    makeTriggerHat('af_trigger_interval_minutes',     b => b.appendDummyInput().appendField(aft('trigger.every', 'every')).appendField(new B.FieldNumber(5, 1, 1440, 1), 'MIN').appendField(aft('trigger.minutes', 'minutes')));
    makeTriggerHat('af_trigger_world_change',         b => b.appendDummyInput().appendField(aft('trigger.world_change', 'when I switch world (15s delay)')));
    makeUserPresenceTriggerHat('af_trigger_user_joins',           aft('trigger.user_joins',           'when someone joins my instance'));
    makeUserPresenceTriggerHat('af_trigger_user_leaves',          aft('trigger.user_leaves',          'when someone leaves my instance'));
    makeUserPresenceTriggerHat('af_trigger_user_joins_or_leaves', aft('trigger.user_joins_or_leaves', 'when someone joins or leaves my instance'));
    makeTriggerHat('af_trigger_own_status_change',    b => b.appendDummyInput().appendField(aft('trigger.own_status_change', 'when my status changes')));
    makeTriggerHat('af_trigger_websocket_any',        b => b.appendDummyInput().appendField(aft('trigger.websocket_any', 'on any websocket event')));
    makeTriggerHat('af_trigger_websocket_friend',     b => b.appendDummyInput().appendField(aft('trigger.websocket_friend', 'on websocket event for friend')).appendField(new B.FieldDropdown(friendDropdown), 'FRIEND_ID'));
    makeTriggerHat('af_trigger_manual',               b => b.appendDummyInput().appendField(aft('trigger.manual', 'manual only (Run Now)')));
    makeTriggerHat('af_trigger_time',                 b => b.appendDummyInput().appendField(aft('trigger.at', 'at'))
        .appendField(new B.FieldNumber(12, 0, 23, 1), 'HH').appendField(':')
        .appendField(new B.FieldNumber(0, 0, 59, 1), 'MM')
        .appendField(new B.FieldDropdown(AMPM_DROPDOWN_FACTORY()), 'AMPM'));
    makeTriggerHat('af_trigger_invite_received',         b => b.appendDummyInput().appendField(aft('trigger.invite_received', 'when someone invites me')));
    makeTriggerHat('af_trigger_invite_request_received', b => b.appendDummyInput().appendField(aft('trigger.invite_request_received', 'when someone requests an invite from me')));
    makeTriggerHat('af_trigger_friend_joins_instance',   b => b.appendDummyInput()
        .appendField(aft('trigger.friend_joins_instance', 'when a friend joins an instance'))
        .appendField(new B.FieldDropdown(friendDropdownAny), 'FRIEND_ID'));

    B.Blocks['af_triggering_user'] = { init() {
        this.appendDummyInput().appendField(aft('triggering_user', 'triggering user'));
        this.setOutput(true, 'User');
        this.setColour(COLOR_TRIGGER);
        this.setTooltip(aft('triggering_user_tooltip', 'The user that caused the current trigger to fire.'));
    } };

    B.Blocks['af_if'] = { init() {
        this.appendValueInput('IF0').setCheck('Boolean').appendField(aft('block.if', 'if'));
        this.appendStatementInput('DO0').appendField(DO);
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_LOGIC);
        this.setTooltip(aft('block.if_tooltip', 'If the condition is true, run the do branch.'));
    } };

    B.Blocks['af_wait_for'] = { init() {
        this.appendValueInput('SECS').setCheck('Number').appendField(aft('block.wait_for', 'wait for'));
        this.appendDummyInput().appendField(aft('block.wait_seconds', 'seconds'));
        this.appendStatementInput('DO').appendField(aft('block.then_do', 'then do'));
        this.setInputsInline(true);
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_LOGIC);
        this.setTooltip(aft('block.wait_for_tooltip', 'Runs the branch after the given number of seconds. Blocks below this one keep running right away. Only one wait per block can be pending at a time.'));
    } };

    B.Blocks['af_cooldown'] = { init() {
        this.appendValueInput('SECS').setCheck('Number').appendField(aft('block.cooldown', 'cooldown'));
        this.appendDummyInput().appendField(aft('block.wait_seconds', 'seconds'));
        this.appendStatementInput('DO').appendField(aft('block.then_do', 'then do'));
        this.setInputsInline(true);
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_LOGIC);
        this.setTooltip(aft('block.cooldown_tooltip', 'Runs the branch right away, then blocks it for the given number of seconds. Use it to stop a trigger that fires often from spamming an action.'));
    } };

    B.Blocks['af_cooldown_else'] = { init() {
        this.appendValueInput('SECS').setCheck('Number').appendField(aft('block.cooldown', 'cooldown'));
        this.appendDummyInput().appendField(aft('block.wait_seconds', 'seconds'));
        this.appendStatementInput('DO').appendField(aft('block.then_do', 'then do'));
        this.appendStatementInput('ELSE').appendField(aft('block.else', 'else'));
        this.setInputsInline(true);
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_LOGIC);
        this.setTooltip(aft('block.cooldown_else_tooltip', 'Like cooldown, but the else branch runs while the cooldown is still active.'));
    } };

    B.Blocks['af_wait_until'] = { init() {
        this.appendValueInput('COND').setCheck('Boolean').appendField(aft('block.wait_until', 'wait until'));
        this.appendValueInput('STATE').setCheck('Boolean').appendField(aft('block.wait_is', 'is'));
        this.appendStatementInput('DO').appendField(aft('block.then_do', 'then do'));
        this.setInputsInline(true);
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_LOGIC);
        this.setTooltip(aft('block.wait_until_tooltip', 'Checks the condition every second and runs the branch once it matches the attached true/false block. Gives up after 30 minutes. Blocks below this one keep running right away.'));
    } };

    B.Blocks['af_if_else'] = { init() {
        this.appendValueInput('IF0').setCheck('Boolean').appendField(aft('block.if', 'if'));
        this.appendStatementInput('DO0').appendField(DO);
        this.appendStatementInput('ELSE').appendField(aft('block.else', 'else'));
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_LOGIC);
    } };

    B.Blocks['af_compare'] = { init() {
        this.appendValueInput('A').setCheck(null);
        this.appendDummyInput().appendField(new B.FieldDropdown([['=','EQ'],['>','GT'],['<','LT']]), 'OP');
        this.appendValueInput('B').setCheck(null);
        this.setOutput(true, 'Boolean');
        this.setInputsInline(true);
        this.setColour(COLOR_LOGIC);
    } };

    B.Blocks['af_and'] = { init() {
        this.appendValueInput('A').setCheck('Boolean');
        this.appendDummyInput().appendField(aft('block.and', 'and'));
        this.appendValueInput('B').setCheck('Boolean');
        this.setOutput(true, 'Boolean');
        this.setInputsInline(true);
        this.setColour(COLOR_LOGIC);
    } };

    B.Blocks['af_or'] = { init() {
        this.appendValueInput('A').setCheck('Boolean');
        this.appendDummyInput().appendField(aft('block.or', 'or'));
        this.appendValueInput('B').setCheck('Boolean');
        this.setOutput(true, 'Boolean');
        this.setInputsInline(true);
        this.setColour(COLOR_LOGIC);
    } };

    B.Blocks['af_bool'] = { init() {
        this.appendDummyInput().appendField(new B.FieldDropdown([
            [aft('block.true',  'true'),  'TRUE'],
            [aft('block.false', 'false'), 'FALSE'],
        ]), 'BOOL');
        this.setOutput(true, 'Boolean');
        this.setColour(COLOR_LOGIC);
    } };

    B.Blocks['af_number'] = { init() {
        this.appendDummyInput().appendField(new B.FieldNumber(0), 'VALUE');
        this.setOutput(true, 'Number');
        this.setColour(COLOR_LOGIC);
        this.setTooltip(aft('block.number_tooltip', 'A numeric literal — use on the right side of comparisons like "user count > 10".'));
    } };

    /* Conditions: persistent named boolean flags managed via the sidebar.
       Both blocks use a dynamic dropdown of currently-defined condition names. */
    const conditionDropdown = () => {
        const names = Object.keys(afConditions).sort();
        if (!names.length) return [[aft('condition.no_conditions', '(no conditions defined)'), '']];
        return names.map(n => [n, n]);
    };

    B.Blocks['af_get_condition'] = { init() {
        this.appendDummyInput()
            .appendField(aft('condition.get', 'condition'))
            .appendField(new B.FieldDropdown(conditionDropdown), 'NAME');
        this.setOutput(true, 'Boolean');
        this.setColour(COLOR_LOGIC);
        this.setTooltip(aft('condition.get_tooltip', 'Returns the current value of a named condition. Define conditions in the Conditions toolbox category.'));
    } };

    B.Blocks['af_set_condition'] = { init() {
        this.appendDummyInput()
            .appendField(aft('condition.set', 'set condition'))
            .appendField(new B.FieldDropdown(conditionDropdown), 'NAME')
            .appendField('=')
            .appendField(new B.FieldDropdown([
                [aft('block.true',  'true'),  'TRUE'],
                [aft('block.false', 'false'), 'FALSE'],
            ]), 'VALUE');
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_LOGIC);
        this.setTooltip(aft('condition.set_tooltip', 'Sets a named condition to true or false. The value persists across app restarts.'));
    } };

    B.Blocks['af_is_date'] = { init() {
        this.appendDummyInput()
            .appendField(aft('time.is_date', 'is date'))
            .appendField(new B.FieldNumber(1, 1, 31, 1), 'DD').appendField('/')
            .appendField(new B.FieldNumber(1, 1, 12, 1), 'MM').appendField('/')
            .appendField(new B.FieldNumber(new Date().getFullYear(), 2000, 2100, 1), 'YYYY');
        this.setOutput(true, 'Boolean');
        this.setColour(COLOR_TIME);
    } };

    B.Blocks['af_is_time'] = { init() {
        this.appendDummyInput()
            .appendField(aft('time.is_time', 'is time'))
            .appendField(new B.FieldNumber(12, 0, 23, 1), 'HH').appendField(':')
            .appendField(new B.FieldNumber(0, 0, 59, 1), 'MM')
            .appendField(new B.FieldDropdown(AMPM_DROPDOWN_FACTORY()), 'AMPM');
        this.setOutput(true, 'Boolean');
        this.setColour(COLOR_TIME);
    } };

    B.Blocks['af_between_time'] = { init() {
        this.appendDummyInput()
            .appendField(aft('time.between', 'between'))
            .appendField(new B.FieldNumber(12, 0, 23, 1), 'HH1').appendField(':')
            .appendField(new B.FieldNumber(0, 0, 59, 1), 'MM1')
            .appendField(new B.FieldDropdown(AMPM_DROPDOWN_FACTORY()), 'AMPM1')
            .appendField(aft('time.between_and', 'and'))
            .appendField(new B.FieldNumber(13, 0, 23, 1), 'HH2').appendField(':')
            .appendField(new B.FieldNumber(0, 0, 59, 1), 'MM2')
            .appendField(new B.FieldDropdown(AMPM_DROPDOWN_FACTORY()), 'AMPM2');
        this.setOutput(true, 'Boolean');
        this.setColour(COLOR_TIME);
        this.setTooltip(aft('time.between_tooltip', 'True while the current time is between the two times. Handles ranges that cross midnight.'));
    } };

    /* Day-of-week filter. Empty selection = "everyday" (always true). */
    B.Blocks['af_on_days'] = { init() {
        this.appendDummyInput()
            .appendField(aft('time.on_days', 'on days'))
            .appendField(aft('day.mo', 'Mo')).appendField(new B.FieldCheckbox('FALSE'), 'DAY_MO')
            .appendField(aft('day.tu', 'Tu')).appendField(new B.FieldCheckbox('FALSE'), 'DAY_TU')
            .appendField(aft('day.we', 'We')).appendField(new B.FieldCheckbox('FALSE'), 'DAY_WE')
            .appendField(aft('day.th', 'Th')).appendField(new B.FieldCheckbox('FALSE'), 'DAY_TH')
            .appendField(aft('day.fr', 'Fr')).appendField(new B.FieldCheckbox('FALSE'), 'DAY_FR')
            .appendField(aft('day.sa', 'Sa')).appendField(new B.FieldCheckbox('FALSE'), 'DAY_SA')
            .appendField(aft('day.su', 'Su')).appendField(new B.FieldCheckbox('FALSE'), 'DAY_SU');
        this.setOutput(true, 'Boolean');
        this.setColour(COLOR_TIME);
        this.setTooltip(aft('time.on_days_tooltip', 'True on any of the checked weekdays. If none are checked, true every day.'));
    } };

    /* Returns whether VRChat.exe is currently running. Backend-polled. */
    B.Blocks['af_is_game_running'] = { init() {
        this.appendDummyInput().appendField(aft('game.is_running', 'is game running'));
        this.setOutput(true, 'Boolean');
        this.setColour(COLOR_GAME);
        this.setTooltip(aft('game.is_running_tooltip', 'True while VRChat.exe is running on this machine. Polled every 5 seconds.'));
    } };

    B.Blocks['af_is_friend'] = { init() {
        this.appendValueInput('USER').setCheck('User').appendField(aft('friend.is_friend', 'is friend'));
        this.setOutput(true, 'Boolean');
        this.setInputsInline(true);
        this.setColour(COLOR_PARAM);
    } };

    B.Blocks['af_is_favorite_friend'] = { init() {
        this.appendValueInput('USER').setCheck('User')
            .appendField(aft('friend.is_favorite', 'is favorite'))
            .appendField(new B.FieldDropdown(favFriendGroupDropdown), 'FAV_GROUP')
            .appendField(aft('friend.is_favorite_suffix', 'friend'));
        this.setOutput(true, 'Boolean');
        this.setInputsInline(true);
        this.setColour(COLOR_PARAM);
        this.setTooltip(aft('friend.is_favorite_tooltip', 'True when the user is in this favorite friend group. Works with both VRChat groups and VRCNext local groups.'));
    } };

    B.Blocks['af_invite_from_friend'] = { init() {
        this.appendDummyInput()
            .appendField(aft('friend.invite_from', 'invite from'))
            .appendField(new B.FieldDropdown(friendDropdown), 'FRIEND_ID');
        this.setOutput(true, 'Boolean');
        this.setColour(COLOR_PARAM);
        this.setTooltip(aft('friend.invite_from_tooltip', 'True inside a "when someone invites me" trigger if the inviter matches this friend.'));
    } };

    B.Blocks['af_invite_request_from_friend'] = { init() {
        this.appendDummyInput()
            .appendField(aft('friend.invite_request_from', 'invite request from'))
            .appendField(new B.FieldDropdown(friendDropdown), 'FRIEND_ID');
        this.setOutput(true, 'Boolean');
        this.setColour(COLOR_PARAM);
        this.setTooltip(aft('friend.invite_request_from_tooltip', 'True inside a "when someone requests an invite from me" trigger if the requester matches this friend.'));
    } };

    B.Blocks['af_friend_obj'] = { init() {
        const opts = () => {
            try {
                if (typeof vrcFriendsData !== 'undefined' && Array.isArray(vrcFriendsData) && vrcFriendsData.length) {
                    return vrcFriendsData
                        .slice()
                        .sort((a, b) => (a.displayName || '').localeCompare(b.displayName || ''))
                        .map(f => [f.displayName || f.id, f.id]);
                }
            } catch {}
            return [[aft('block.no_friends', '(no friends loaded)'), '']];
        };
        this.appendDummyInput().appendField(aft('friend.friend', 'friend')).appendField(new B.FieldDropdown(opts), 'FRIEND_ID');
        this.setOutput(true, 'User');
        this.setColour(COLOR_PARAM);
    } };

    B.Blocks['af_user_obj'] = { init() {
        this.appendDummyInput().appendField(aft('friend.user', 'user')).appendField(new B.FieldTextInput('usr_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'), 'USER_ID');
        this.setOutput(true, 'User');
        this.setColour(COLOR_PARAM);
    } };

    B.Blocks['af_own_user'] = { init() {
        this.appendDummyInput().appendField(aft('friend.me', 'me'));
        this.setOutput(true, 'User');
        this.setColour(COLOR_PARAM);
    } };

    B.Blocks['af_world_obj'] = { init() {
        this.appendDummyInput().appendField(aft('world_block.world', 'world')).appendField(new B.FieldTextInput('wrld_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'), 'WORLD_ID');
        this.setOutput(true, 'World');
        this.setColour(COLOR_PARAM);
    } };

    B.Blocks['af_has_status'] = { init() {
        this.appendValueInput('USER').setCheck('User').appendField(aft('status_block.has_status', 'has status'));
        this.appendDummyInput().appendField(new B.FieldDropdown(STATUS_DROPDOWN_FACTORY()), 'STATUS');
        this.setOutput(true, 'Boolean');
        this.setInputsInline(true);
        this.setColour(COLOR_PARAM);
    } };

    B.Blocks['af_own_status'] = { init() {
        this.appendDummyInput().appendField(aft('status_block.own_status_eq', 'own status =')).appendField(new B.FieldDropdown(STATUS_DROPDOWN_FACTORY()), 'STATUS');
        this.setOutput(true, 'Boolean');
        this.setColour(COLOR_PARAM);
    } };

    B.Blocks['af_has_status_text'] = { init() {
        this.appendValueInput('USER').setCheck('User').appendField(aft('status_block.has_status_text', 'has status text'));
        this.appendDummyInput().appendField('"').appendField(new B.FieldTextInput(''), 'TEXT').appendField('"');
        this.setOutput(true, 'Boolean');
        this.setInputsInline(true);
        this.setColour(COLOR_PARAM);
    } };

    B.Blocks['af_own_status_text'] = { init() {
        this.appendDummyInput().appendField(aft('status_block.own_status_text_eq', 'own status text =')).appendField('"').appendField(new B.FieldTextInput(''), 'TEXT').appendField('"');
        this.setOutput(true, 'Boolean');
        this.setColour(COLOR_PARAM);
    } };

    B.Blocks['af_has_bio_text'] = { init() {
        this.appendValueInput('USER').setCheck('User').appendField(aft('status_block.has_bio_text', 'has bio text'));
        this.appendDummyInput().appendField('"').appendField(new B.FieldTextInput(''), 'TEXT').appendField('"');
        this.setOutput(true, 'Boolean');
        this.setInputsInline(true);
        this.setColour(COLOR_PARAM);
    } };

    B.Blocks['af_own_bio_text'] = { init() {
        this.appendDummyInput().appendField(aft('status_block.own_bio_text_eq', 'own bio text =')).appendField('"').appendField(new B.FieldTextInput(''), 'TEXT').appendField('"');
        this.setOutput(true, 'Boolean');
        this.setColour(COLOR_PARAM);
    } };

    B.Blocks['af_get_current_world'] = { init() {
        this.appendValueInput('USER').setCheck('User').appendField(aft('world_block.current_world_of', 'current world of'));
        this.setOutput(true, 'World');
        this.setInputsInline(true);
        this.setColour(COLOR_PARAM);
    } };

    B.Blocks['af_in_same_instance'] = { init() {
        this.appendValueInput('USER').setCheck('User').appendField(aft('world_block.in_same_instance', 'is in same instance as me'));
        this.setOutput(true, 'Boolean');
        this.setInputsInline(true);
        this.setColour(COLOR_PARAM);
    } };

    B.Blocks['af_get_my_avatar'] = { init() {
        this.appendDummyInput().appendField(aft('avatar_block.my_current', 'my current avatar'));
        this.setOutput(true, 'String');
        this.setColour(COLOR_PARAM);
        this.setTooltip(aft('avatar_block.my_current_tooltip', 'Returns the ID of the avatar I am currently wearing.'));
    } };

    B.Blocks['af_avatar_obj'] = { init() {
        this.appendDummyInput()
            .appendField(aft('avatar_block.avatar', 'avatar'))
            .appendField(new B.FieldTextInput('avtr_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'), 'AVATAR_ID');
        this.setOutput(true, 'String');
        this.setColour(COLOR_PARAM);
        this.setTooltip(aft('avatar_block.avatar_tooltip', 'A literal avatar ID — use on the right side of "= my current avatar" to compare.'));
    } };

    B.Blocks['af_is_my_avatar'] = { init() {
        this.appendDummyInput()
            .appendField(aft('avatar_block.is_my_current', 'is my current avatar'))
            .appendField(new B.FieldTextInput('avtr_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'), 'AVATAR_ID');
        this.setOutput(true, 'Boolean');
        this.setColour(COLOR_PARAM);
        this.setTooltip(aft('avatar_block.is_my_current_tooltip', 'True if the given avatar ID matches my currently worn avatar.'));
    } };

    B.Blocks['af_get_user_count'] = { init() {
        this.appendDummyInput().appendField(aft('instance_block.user_count', 'user count of instance'));
        this.setOutput(true, 'Number');
        this.setColour(COLOR_PARAM);
        this.setTooltip(aft('instance_block.user_count_tooltip', 'Number of users currently in my instance (includes me).'));
    } };

    B.Blocks['af_get_instance_type'] = { init() {
        this.appendDummyInput().appendField(aft('instance_block.my_type', 'my instance type'));
        this.setOutput(true, 'String');
        this.setColour(COLOR_PARAM);
        this.setTooltip(aft('instance_block.my_type_tooltip', 'Returns the instance type I am currently in (e.g., public, friends, invite_plus, group-plus).'));
    } };

    /* Instance type literal — dropdown of all VRChat instance types. Label is
       user-facing; value matches the canonical strings used in parseFriendLocation
       (`public`, `friends`, `friends+`, `invite_plus`, `private`, `group`,
       `group-plus`, `group-public`). */
    const INSTANCE_TYPE_DROPDOWN_FACTORY = () => () => [
        [aft('instance_type.public',       'Public'),       'public'],
        [aft('instance_type.friends_plus', 'Friends+'),     'friends+'],
        [aft('instance_type.friends',      'Friends'),      'friends'],
        [aft('instance_type.invite_plus',  'Invite+'),      'invite_plus'],
        [aft('instance_type.invite',       'Invite'),       'private'],
        [aft('instance_type.group_public', 'Group Public'), 'group-public'],
        [aft('instance_type.group_plus',   'Group+'),       'group-plus'],
        [aft('instance_type.group',        'Group'),        'group'],
    ];

    B.Blocks['af_instance_type_obj'] = { init() {
        this.appendDummyInput()
            .appendField(aft('instance_block.type_literal', 'instance type'))
            .appendField(new B.FieldDropdown(INSTANCE_TYPE_DROPDOWN_FACTORY()), 'INSTANCE_TYPE');
        this.setOutput(true, 'String');
        this.setColour(COLOR_PARAM);
        this.setTooltip(aft('instance_block.type_literal_tooltip', 'A literal instance type — use on the right side of "= my instance type" to compare.'));
    } };

    B.Blocks['af_set_status'] = { init() {
        this.appendDummyInput()
            .appendField(aft('action.set_status', 'set status')).appendField(new B.FieldDropdown(STATUS_DROPDOWN_FACTORY()), 'STATUS')
            .appendField(aft('action.text', 'text')).appendField('"').appendField(new B.FieldTextInput(''), 'TEXT').appendField('"');
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_ACTION);
        this.setTooltip(aft('action.set_status_tooltip', 'Sets your VRChat status (and status text if non-empty). Empty text keeps the existing status text.'));
    } };

    B.Blocks['af_set_bio_text'] = { init() {
        this.appendDummyInput().appendField(aft('action.set_bio_text', 'set bio text')).appendField('"').appendField(new B.FieldTextInput(''), 'TEXT').appendField('"');
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_ACTION);
    } };

    B.Blocks['af_invite_friend'] = { init() {
        this.appendValueInput('USER').setCheck('User').appendField(aft('action.invite', 'invite'));
        this.appendDummyInput().appendField(aft('action.to_my_instance', 'to my instance'));
        this.setInputsInline(true);
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_ACTION);
    } };

    B.Blocks['af_request_invite'] = { init() {
        this.appendValueInput('USER').setCheck('User').appendField(aft('action.request_invite_from', 'request invite from'));
        this.setInputsInline(true);
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_ACTION);
    } };

    B.Blocks['af_send_notification'] = { init() {
        this.appendDummyInput().appendField(aft('action.send_notification', 'send notification')).appendField('"').appendField(new B.FieldTextInput(aft('action.send_notification_default', 'Hello')), 'TEXT').appendField('"');
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_VRCN);
    } };

    B.Blocks['af_answer_invite'] = { init() {
        this.appendDummyInput().appendField(aft('action.answer_invite', 'answer invite')).appendField('"').appendField(new B.FieldTextInput(aft('action.answer_invite_default', "Sorry, can't right now!")), 'TEXT').appendField('"');
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_ACTION);
        this.setTooltip(aft('action.answer_invite_tooltip', 'Send a chat message back to the user who invited me. Use inside a "when someone invites me" trigger.'));
    } };

    B.Blocks['af_answer_invite_request'] = { init() {
        this.appendDummyInput().appendField(aft('action.answer_invite_request', 'answer invite request')).appendField('"').appendField(new B.FieldTextInput(aft('action.answer_invite_request_default', 'Sorry, no invite right now!')), 'TEXT').appendField('"');
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_ACTION);
        this.setTooltip(aft('action.answer_invite_request_tooltip', 'Send a chat message back to the user who requested an invite. Use inside a "when someone requests an invite" trigger.'));
    } };

    const ownAvatarDropdown = () => {
        try {
            if (typeof avatarsData !== 'undefined' && Array.isArray(avatarsData) && avatarsData.length) {
                return avatarsData
                    .slice()
                    .sort((a, b) => (a.name || '').localeCompare(b.name || ''))
                    .map(a => [a.name || a.id, a.id]);
            }
        } catch {}
        return [[aft('action.no_avatars', '(no avatars loaded)'), '']];
    };
    const favoriteAvatarDropdown = () => {
        try {
            if (typeof favAvatarsData !== 'undefined' && Array.isArray(favAvatarsData) && favAvatarsData.length) {
                return favAvatarsData
                    .slice()
                    .sort((a, b) => (a.name || '').localeCompare(b.name || ''))
                    .map(a => [a.name || a.id, a.id]);
            }
        } catch {}
        return [[aft('action.no_favorite_avatars', '(no favorite avatars loaded)'), '']];
    };

    B.Blocks['af_switch_own_avatar'] = { init() {
        this.appendDummyInput()
            .appendField(aft('action.switch_own_avatar', 'switch to my avatar'))
            .appendField(new B.FieldDropdown(ownAvatarDropdown), 'AVATAR_ID');
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_ACTION);
        this.setTooltip(aft('action.switch_own_avatar_tooltip', 'Switch to one of your own uploaded avatars.'));
    } };

    B.Blocks['af_switch_favorite_avatar'] = { init() {
        this.appendDummyInput()
            .appendField(aft('action.switch_favorite_avatar', 'switch to favorite avatar'))
            .appendField(new B.FieldDropdown(favoriteAvatarDropdown), 'AVATAR_ID');
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_ACTION);
        this.setTooltip(aft('action.switch_favorite_avatar_tooltip', 'Switch to one of your favorited avatars.'));
    } };

    B.Blocks['af_send_own_instance_info'] = { init() {
        this.appendDummyInput()
            .appendField(aft('action.send_own_instance_info', 'send own instance info to'))
            .appendField('"').appendField(new B.FieldTextInput(''), 'WEBHOOK').appendField('"').appendField(aft('action.webhook_hint', 'discord webhook url'));
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_OTHER);
        this.setTooltip(aft('action.send_own_instance_info_tooltip', 'Sends your current instance (world image, name, type, player count) to a Discord webhook. Uses cached data, no VRChat request.'));
    } };

    B.Blocks['af_send_own_advanced_instance_info'] = { init() {
        this.appendDummyInput()
            .appendField(aft('action.send_own_advanced_instance_info', 'send own advanced instance info to'))
            .appendField('"').appendField(new B.FieldTextInput(''), 'WEBHOOK').appendField('"').appendField(aft('action.webhook_hint', 'discord webhook url'));
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_OTHER);
        this.setTooltip(aft('action.send_own_advanced_instance_info_tooltip', 'Like send own instance info, plus the player list. Uses cached data, no VRChat request.'));
    } };

    B.Blocks['af_send_friend_instance_info'] = { init() {
        this.appendDummyInput()
            .appendField(aft('action.send_friend_instance_info', 'send friend instance info to'))
            .appendField('"').appendField(new B.FieldTextInput(''), 'WEBHOOK').appendField('"').appendField(aft('action.webhook_hint', 'discord webhook url'));
        this.appendDummyInput()
            .appendField(aft('action.friend_label', 'friend'))
            .appendField(new B.FieldDropdown(friendDropdownAny), 'FRIEND_ID');
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_OTHER);
        this.setTooltip(aft('action.send_friend_instance_info_tooltip', 'Sends a friend instance info to a Discord webhook. Keep friend on (any friend) to use the friend from a "when a friend joins an instance" trigger.'));
    } };

    B.Blocks['af_set_feature'] = { init() {
        this.appendValueInput('STATE').setCheck('Boolean')
            .appendField(aft('action.set_feature', 'set feature'))
            .appendField(new B.FieldDropdown(FEATURE_DROPDOWN_FACTORY), 'FEATURE')
            .appendField(aft('action.set_feature_to', 'to'));
        this.setInputsInline(true);
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_VRCN);
        this.setTooltip(aft('action.set_feature_tooltip', 'Turns a VRCNext feature on or off. true starts it, false stops it. Does nothing when the feature is already in that state.'));
    } };

    function makeOscSetBlock(typeName, labelKey, labelFallback, wantType, check) {
        B.Blocks[typeName] = { init() {
            this.appendValueInput('VALUE').setCheck(check)
                .appendField(aft(labelKey, labelFallback))
                .appendField('"').appendField(new B.FieldTextInput(''), 'PARAM').appendField('"')
                .appendField(new B.FieldDropdown(oscParamPicker(wantType), oscPickValidator), 'PICK')
                .appendField(aft('osc.to', 'to'));
            this.setInputsInline(true);
            this.setPreviousStatement(true, null);
            this.setNextStatement(true, null);
            this.setColour(COLOR_VRCN);
            this.setTooltip(aft('osc.tooltip', 'Sends an OSC value to VRChat. A plain name is sent as an avatar parameter, a path starting with / is sent as is, so addresses like /input/Jump or /chatbox/typing work too. Requires the OSC Tool to be connected.'));
        } };
    }
    makeOscSetBlock('af_osc_set_bool',  'osc.set_bool',  'set OSC bool',    'bool',  'Boolean');
    makeOscSetBlock('af_osc_set_float', 'osc.set_float', 'set OSC float',   'float', 'Number');
    makeOscSetBlock('af_osc_set_int',   'osc.set_int',   'set OSC integer', 'int',   'Number');

    B.Blocks['af_send_webhook'] = { init() {
        this.appendDummyInput()
            .appendField(aft('action.send_webhook', 'send to webhook'))
            .appendField('"').appendField(new B.FieldTextInput(''), 'WEBHOOK').appendField('"').appendField(aft('action.webhook_hint', 'discord webhook url'))
            .appendField('"').appendField(new B.FieldTextInput(aft('action.send_webhook_default', 'Hello')), 'TEXT').appendField('"');
        this.setInputsInline(true);
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_OTHER);
        this.setTooltip(aft('action.send_webhook_tooltip', 'Sends your text to a Discord webhook. Useful for long term logging instead of a temporary notification.'));
    } };

    B.Blocks['af_send_webhook_value'] = { init() {
        this.appendDummyInput()
            .appendField(aft('action.send_webhook', 'send to webhook'))
            .appendField('"').appendField(new B.FieldTextInput(''), 'WEBHOOK').appendField('"').appendField(aft('action.webhook_hint', 'discord webhook url'));
        this.appendValueInput('VALUE');
        this.setInputsInline(true);
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_OTHER);
        this.setTooltip(aft('action.send_webhook_value_tooltip', 'Sends the value of the attached Get Info block to a Discord webhook.'));
    } };

    B.Blocks['af_send_advanced_webhook'] = { init() {
        this.appendDummyInput()
            .appendField(aft('action.send_webhook', 'send to webhook'))
            .appendField('"').appendField(new B.FieldTextInput(''), 'WEBHOOK').appendField('"').appendField(aft('action.webhook_hint', 'discord webhook url'))
            .appendField('"').appendField(new B.FieldTextInput(aft('action.send_advanced_webhook_default', 'Info:')), 'TEXT').appendField('"');
        this.appendValueInput('VALUE');
        this.setInputsInline(true);
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_OTHER);
        this.setTooltip(aft('action.send_advanced_webhook_tooltip', 'Sends your own text followed by the value of the attached Get Info block to a Discord webhook.'));
    } };

    B.Blocks['af_switch_avatar_id'] = { init() {
        this.appendDummyInput()
            .appendField(aft('action.switch_to_avatar', 'switch to avatar'))
            .appendField(new B.FieldTextInput('avtr_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'), 'AVATAR_ID');
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_ACTION);
        this.setTooltip(aft('action.switch_to_avatar_tooltip', 'Switches to the given avatar ID. The avatar has to be your own or public.'));
    } };

    B.Blocks['af_set_home_world'] = { init() {
        this.appendDummyInput().appendField(aft('action.set_home_world', 'set current world as home world'));
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_ACTION);
        this.setTooltip(aft('action.set_home_world_tooltip', 'Sets the world you are currently in as your home world.'));
    } };

    B.Blocks['af_close_vrchat'] = { init() {
        this.appendDummyInput().appendField(aft('game.close_vrchat', 'close VRChat'));
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_GAME);
        this.setTooltip(aft('game.close_vrchat_tooltip', 'Closes VRChat if it is running. Uses no VRChat API call.'));
    } };

    B.Blocks['af_send_notification_value'] = { init() {
        this.appendValueInput('VALUE').appendField(aft('action.send_notification', 'send notification'));
        this.setInputsInline(true);
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_VRCN);
        this.setTooltip(aft('action.send_notification_value_tooltip', 'Sends a notification containing the value of the attached Get Info block.'));
    } };

    B.Blocks['af_send_advanced_notification'] = { init() {
        this.appendDummyInput()
            .appendField(aft('action.send_advanced_notification', 'send notification'))
            .appendField('"').appendField(new B.FieldTextInput(aft('action.send_advanced_notification_default', 'Info:')), 'TEXT').appendField('"');
        this.appendValueInput('VALUE');
        this.setInputsInline(true);
        this.setPreviousStatement(true, null);
        this.setNextStatement(true, null);
        this.setColour(COLOR_VRCN);
        this.setTooltip(aft('action.send_advanced_notification_tooltip', 'Sends a notification with your own text followed by the value of the attached Get Info block.'));
    } };

    const AF_BUNDLE_MAX = 20;
    B.Blocks['af_bundle_list'] = {
        init() {
            this.appendDummyInput('HEAD').appendField(aft('info.bundle_list', 'bundled message list:'));
            this.itemCount_ = 1;
            this.updateShape_();
            this.setOutput(true, 'String');
            this.setColour(COLOR_INFO);
            this.setTooltip(aft('info.bundle_list_tooltip', 'Collects several Get Info blocks into one message. Each entry becomes its own line. Plug it into a send notification or send to webhook block.'));
        },
        saveExtraState() {
            return { itemCount: this.itemCount_ };
        },
        loadExtraState(state) {
            this.itemCount_ = Math.max(1, Math.min(AF_BUNDLE_MAX, (state && state.itemCount) || 1));
            this.updateShape_();
        },
        updateShape_() {
            for (let i = 0; i < this.itemCount_; i++) {
                if (!this.getInput('ITEM' + i)) this.appendValueInput('ITEM' + i).appendField('>');
            }
            let i = this.itemCount_;
            while (this.getInput('ITEM' + i)) { this.removeInput('ITEM' + i); i++; }
        },
        onchange(e) {
            if (!this.workspace || this.isInFlyout) return;
            if (afAutoSaveSuppressed) return;
            if (e && e.isUiEvent) return;
            let used = 0;
            for (let i = 0; i < this.itemCount_; i++) {
                const inp = this.getInput('ITEM' + i);
                if (inp && inp.connection && inp.connection.targetBlock()) used = i + 1;
            }
            const want = Math.max(1, Math.min(AF_BUNDLE_MAX, used + 1));
            if (want === this.itemCount_) return;
            this.itemCount_ = want;
            this.updateShape_();
        },
    };

    B.Blocks['af_text'] = { init() {
        this.appendDummyInput()
            .appendField('"').appendField(new B.FieldTextInput(''), 'TEXT').appendField('"');
        this.setOutput(true, 'String');
        this.setColour(COLOR_INFO);
        this.setTooltip(aft('info.text_tooltip', 'A piece of text you type yourself. Use it to label an entry in a bundled message list.'));
    } };

    B.Blocks['af_join_text'] = { init() {
        this.appendValueInput('A');
        this.appendValueInput('B').appendField(aft('block.and', 'and'));
        this.setInputsInline(true);
        this.setOutput(true, 'String');
        this.setColour(COLOR_INFO);
        this.setTooltip(aft('info.join_text_tooltip', 'Glues two values into one line of text. A space is added between them unless the left side already ends with one.'));
    } };

    function makeFriendInfoBlock(typeName, labelKey, labelFallback, tipKey, tipFallback) {
        B.Blocks[typeName] = { init() {
            this.appendDummyInput()
                .appendField(aft(labelKey, labelFallback))
                .appendField(new B.FieldDropdown(friendDropdownTrigger), 'FRIEND_ID');
            this.setOutput(true, 'String');
            this.setInputsInline(true);
            this.setColour(COLOR_FRIEND);
            this.setTooltip(aft(tipKey, tipFallback));
        } };
    }
    makeFriendInfoBlock('af_friend_status',      'friend_info.status',      "friend's status is",
                        'friend_info.status_tooltip',      'The status of the picked friend: Online, Ask Me, Do Not Disturb or Join Me.');
    makeFriendInfoBlock('af_friend_status_text', 'friend_info.status_text', "friend's status text is",
                        'friend_info.status_text_tooltip', 'The status text the picked friend wrote under their status.');
    makeFriendInfoBlock('af_friend_world',       'friend_info.world',       "friend's current world is",
                        'friend_info.world_tooltip',       'The world the picked friend is in. Empty when they are offline or in a private instance.');
    makeFriendInfoBlock('af_friend_presence',    'friend_info.presence',    "friend is",
                        'friend_info.presence_tooltip',    'Whether the picked friend is In Game, Active on Website or Offline.');

    function makePlayerImageBlock(typeName, labelKey, labelFallback, tipKey, tipFallback) {
        B.Blocks[typeName] = { init() {
            this.appendDummyInput().appendField(aft(labelKey, labelFallback));
            this.setOutput(true, 'Icon');
            this.setColour(COLOR_FRIEND);
            this.setTooltip(aft(tipKey, tipFallback));
        } };
    }
    makePlayerImageBlock('af_get_joined_player_image', 'info.joined_player_image', 'joined player image',
        'info.joined_player_image_tooltip', 'Profile picture of the player from the trigger, for example the one who just joined your instance. Pair it with joined player name.');
    makePlayerImageBlock('af_get_left_player_image', 'info.left_player_image', 'left player image',
        'info.left_player_image_tooltip', 'Profile picture of the player who just left your instance. Empty when the trigger was not a leave.');

    B.Blocks['af_friend_icon'] = { init() {
        this.appendDummyInput()
            .appendField(aft('friend_info.icon', "friend's icon"))
            .appendField(new B.FieldDropdown(friendDropdownTrigger), 'FRIEND_ID');
        this.setOutput(true, 'Icon');
        this.setInputsInline(true);
        this.setColour(COLOR_FRIEND);
        this.setTooltip(aft('friend_info.icon_tooltip', "Uses the friend's profile picture as the image of the message. Works in send notification and send to webhook, and shows up in the in-app card, the system tray notification, the VR overlay and the Discord webhook."));
    } };

    const INFO_BLOCKS = [
        ['af_get_world_name',     'info.world_name',     'current world name',     'info.world_name_tooltip',     'Name of the world you are currently in.'],
        ['af_get_avatar_name',    'info.avatar_name',    'current avatar name',    'info.avatar_name_tooltip',    'Name of the avatar you are currently wearing.'],
        ['af_get_instance_name',  'info.instance_name',  'current instance name',  'info.instance_name_tooltip',  'Name the instance owner gave this instance. Empty when the instance has no name.'],
        ['af_get_instance_id',    'info.instance_id',    'current instance ID',    'info.instance_id_tooltip',    'ID of the instance you are currently in.'],
        ['af_get_player_count',   'info.player_count',   'current player count',   'info.player_count_tooltip',   'Players in your instance as count/capacity.'],
        ['af_get_player_list',    'info.player_list',    'current player list',    'info.player_list_tooltip',    'Names of everyone in your instance, separated by commas.'],
        ['af_get_ingame_friends', 'info.ingame_friends', 'friends in game',        'info.ingame_friends_tooltip', 'Names of your friends that are currently in VRChat, separated by commas.'],
        ['af_get_time',           'info.time',           'current time',           'info.time_tooltip',           'Your current local time.'],
        ['af_get_joined_player_name', 'info.joined_player_name', 'joined player name', 'info.joined_player_name_tooltip', 'Name of the player from the trigger, for example the one who just joined your instance.'],
        ['af_get_left_player_name', 'info.left_player_name', 'left player name', 'info.left_player_name_tooltip', 'Name of the player who just left your instance. Empty when the trigger was not a leave.'],
        ['af_get_instance_type_label', 'info.instance_type', 'current instance type', 'info.instance_type_tooltip', 'Instance type you are currently in as readable text, for example Friends+ or Group Public.'],
    ];
    for (const [type, labelKey, labelFb, tipKey, tipFb] of INFO_BLOCKS) {
        B.Blocks[type] = { init() {
            this.appendDummyInput().appendField(aft(labelKey, labelFb) + ' (string)');
            this.setOutput(true, 'String');
            this.setColour(COLOR_INFO);
            this.setTooltip(aft(tipKey, tipFb));
        } };
    }

    B.Blocks['af_get_heart_rate_int'] = { init() {
        this.appendDummyInput().appendField(aft('info.heart_rate', 'heart rate') + ' (int)');
        this.setOutput(true, 'Number');
        this.setColour(COLOR_VRCN);
        this.setTooltip(aft('info.heart_rate_tooltip', 'Current heart rate in BPM from the HypeRate connection of the Custom Chatbox. 0 when no data is available. Enable "Allow Action Flow using Heart Rate" in the Custom Chatbox to use it while the chatbox is off.'));
    } };
}

function afToolbox() {
    return {
        kind: 'categoryToolbox',
        contents: [
            { kind: 'category', name: aft('toolbox.triggers', 'Triggers'), colour: COLOR_TRIGGER, contents: [
                { kind: 'block', type: 'af_trigger_interval_30s' },
                { kind: 'block', type: 'af_trigger_interval_minutes' },
                { kind: 'block', type: 'af_trigger_time' },
                { kind: 'block', type: 'af_trigger_world_change' },
                { kind: 'block', type: 'af_trigger_user_joins' },
                { kind: 'block', type: 'af_trigger_user_leaves' },
                { kind: 'block', type: 'af_trigger_user_joins_or_leaves' },
                { kind: 'block', type: 'af_trigger_own_status_change' },
                { kind: 'block', type: 'af_trigger_websocket_any' },
                { kind: 'block', type: 'af_trigger_websocket_friend' },
                { kind: 'block', type: 'af_trigger_invite_received' },
                { kind: 'block', type: 'af_trigger_invite_request_received' },
                { kind: 'block', type: 'af_trigger_friend_joins_instance' },
                { kind: 'block', type: 'af_trigger_manual' },
                { kind: 'sep' },
                { kind: 'block', type: 'af_triggering_user' },
            ]},
            { kind: 'category', name: aft('toolbox.logic', 'Logic'), colour: COLOR_LOGIC, contents: [
                { kind: 'block', type: 'af_if' },
                { kind: 'block', type: 'af_if_else' },
                { kind: 'block', type: 'af_wait_for' },
                { kind: 'block', type: 'af_wait_until' },
                { kind: 'block', type: 'af_cooldown' },
                { kind: 'block', type: 'af_cooldown_else' },
                { kind: 'block', type: 'af_compare' },
                { kind: 'block', type: 'af_and' },
                { kind: 'block', type: 'af_or' },
                { kind: 'block', type: 'af_bool' },
                { kind: 'block', type: 'af_number' },
                { kind: 'sep' },
                { kind: 'block', type: 'af_get_condition' },
                { kind: 'block', type: 'af_set_condition' },
            ]},
            { kind: 'category', name: aft('toolbox.time', 'Time'), colour: COLOR_TIME, contents: [
                { kind: 'block', type: 'af_is_date' },
                { kind: 'block', type: 'af_is_time' },
                { kind: 'block', type: 'af_between_time' },
                { kind: 'block', type: 'af_on_days' },
            ]},
            { kind: 'category', name: aft('toolbox.game', 'Game'), colour: COLOR_GAME, contents: [
                { kind: 'block', type: 'af_is_game_running' },
                { kind: 'block', type: 'af_close_vrchat' },
            ]},
            { kind: 'category', name: aft('toolbox.get_info', 'Get Info'), colour: COLOR_INFO, contents: [
                { kind: 'block', type: 'af_bundle_list' },
                { kind: 'block', type: 'af_text' },
                { kind: 'block', type: 'af_join_text' },
                { kind: 'sep' },
                { kind: 'block', type: 'af_friend_status' },
                { kind: 'block', type: 'af_friend_status_text' },
                { kind: 'block', type: 'af_friend_world' },
                { kind: 'block', type: 'af_friend_presence' },
                { kind: 'block', type: 'af_friend_icon' },
                { kind: 'block', type: 'af_get_world_name' },
                { kind: 'block', type: 'af_get_avatar_name' },
                { kind: 'block', type: 'af_get_instance_name' },
                { kind: 'block', type: 'af_get_instance_id' },
                { kind: 'block', type: 'af_get_player_count' },
                { kind: 'block', type: 'af_get_player_list' },
                { kind: 'block', type: 'af_get_ingame_friends' },
                { kind: 'block', type: 'af_get_time' },
                { kind: 'block', type: 'af_get_joined_player_name' },
                { kind: 'block', type: 'af_get_left_player_name' },
                { kind: 'block', type: 'af_get_joined_player_image' },
                { kind: 'block', type: 'af_get_left_player_image' },
                { kind: 'block', type: 'af_get_instance_type_label' },
                { kind: 'block', type: 'af_get_heart_rate_int' },
            ]},
            { kind: 'category', name: aft('toolbox.friends', 'Friends'), colour: COLOR_PARAM, contents: [
                { kind: 'block', type: 'af_friend_obj' },
                { kind: 'block', type: 'af_user_obj' },
                { kind: 'block', type: 'af_own_user' },
                { kind: 'block', type: 'af_is_friend' },
                { kind: 'block', type: 'af_is_favorite_friend' },
                { kind: 'block', type: 'af_invite_from_friend' },
                { kind: 'block', type: 'af_invite_request_from_friend' },
            ]},
            { kind: 'category', name: aft('toolbox.status_bio', 'Status & Bio'), colour: COLOR_PARAM, contents: [
                { kind: 'block', type: 'af_has_status' },
                { kind: 'block', type: 'af_own_status' },
                { kind: 'block', type: 'af_has_status_text' },
                { kind: 'block', type: 'af_own_status_text' },
                { kind: 'block', type: 'af_has_bio_text' },
                { kind: 'block', type: 'af_own_bio_text' },
            ]},
            { kind: 'category', name: aft('toolbox.world', 'World'), colour: COLOR_PARAM, contents: [
                { kind: 'block', type: 'af_world_obj' },
                { kind: 'block', type: 'af_get_current_world' },
                { kind: 'block', type: 'af_in_same_instance' },
            ]},
            { kind: 'category', name: aft('toolbox.avatar', 'Avatar'), colour: COLOR_PARAM, contents: [
                { kind: 'block', type: 'af_get_my_avatar' },
                { kind: 'block', type: 'af_avatar_obj' },
                { kind: 'block', type: 'af_is_my_avatar' },
            ]},
            { kind: 'category', name: aft('toolbox.instance', 'Instance'), colour: COLOR_PARAM, contents: [
                { kind: 'block', type: 'af_get_user_count' },
                { kind: 'block', type: 'af_get_instance_type' },
                { kind: 'block', type: 'af_instance_type_obj' },
            ]},
            { kind: 'category', name: aft('toolbox.actions', 'VRC Actions'), colour: COLOR_ACTION, contents: [
                { kind: 'block', type: 'af_set_status' },
                { kind: 'block', type: 'af_set_bio_text' },
                { kind: 'block', type: 'af_switch_own_avatar' },
                { kind: 'block', type: 'af_switch_favorite_avatar' },
                { kind: 'block', type: 'af_switch_avatar_id' },
                { kind: 'block', type: 'af_set_home_world' },
                { kind: 'block', type: 'af_invite_friend' },
                { kind: 'block', type: 'af_request_invite' },
                { kind: 'block', type: 'af_answer_invite' },
                { kind: 'block', type: 'af_answer_invite_request' },
            ]},
            { kind: 'category', name: aft('toolbox.vrcn_actions', 'VRCN Actions'), colour: COLOR_VRCN, contents: [
                { kind: 'block', type: 'af_trigger_every_second' },
                { kind: 'sep' },
                { kind: 'block', type: 'af_send_notification' },
                { kind: 'block', type: 'af_send_notification_value' },
                { kind: 'block', type: 'af_send_advanced_notification' },
                { kind: 'block', type: 'af_set_feature' },
                { kind: 'block', type: 'af_osc_set_bool' },
                { kind: 'block', type: 'af_osc_set_float' },
                { kind: 'block', type: 'af_osc_set_int' },
            ]},
            { kind: 'category', name: aft('toolbox.other', 'Webhook Actions'), colour: COLOR_OTHER, contents: [
                { kind: 'block', type: 'af_send_own_instance_info' },
                { kind: 'block', type: 'af_send_own_advanced_instance_info' },
                { kind: 'block', type: 'af_send_friend_instance_info' },
                { kind: 'block', type: 'af_send_webhook' },
                { kind: 'block', type: 'af_send_webhook_value' },
                { kind: 'block', type: 'af_send_advanced_webhook' },
            ]},
        ],
    };
}

function afPauseSvgAnimations() {
    const host = document.getElementById('afBlocklyHost');
    if (!host) return;
    host.querySelectorAll('svg').forEach(s => { try { s.pauseAnimations(); } catch {} });
}

async function afInitWorkspace() {
    if (afWorkspace) return;
    await afEnsureBlockly();
    afDefineBlocks();
    const host = document.getElementById('afBlocklyHost');
    if (!host) return;
    const cssVar = (name, fallback) =>
        getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
    afWorkspace = window.Blockly.inject(host, {
        toolbox:   afToolbox(),
        trashcan:  false,
        sounds:    false,
        zoom:      { controls: false, wheel: true, startScale: 0.95, maxScale: 2, minScale: 0.5, scaleSpeed: 1.1 },
        move:      { scrollbars: true, drag: true, wheel: false },
        grid:      { spacing: 24, length: 3, colour: cssVar('--brd', '#3a3f4a'), snap: true },
        renderer:  'zelos',
        theme: window.Blockly.Theme.defineTheme('vrcnext', {
            base: window.Blockly.Themes.Classic,
            componentStyles: {
                workspaceBackgroundColour: cssVar('--bg-input', '#1f2330'),
                toolboxBackgroundColour:   cssVar('--bg-card',  '#2a2e3a'),
                flyoutBackgroundColour:    cssVar('--bg-card',  '#2a2e3a'),
                flyoutForegroundColour:    cssVar('--tx0',      '#ffffff'),
                scrollbarColour:           cssVar('--brd',      '#666'),
                insertionMarkerColour:     cssVar('--accent',   '#ffffff'),
                insertionMarkerOpacity:    0.5,
                cursorColour:              cssVar('--accent',   '#ffffff'),
                selectedGlowColour:        '#7dd1ff',
                selectedGlowSize:          0.8,
                replacementGlowColour:     '#7dd1ff',
                replacementGlowSize:       2,
            },
        }),
    });
    afWorkspace.addChangeListener(afOnWorkspaceChange);
    afPauseSvgAnimations();
    afWorkspace.configureContextMenu = (menuOptions) => { menuOptions.length = 0; };
    if (typeof ResizeObserver !== 'undefined') {
        try {
            const ro = new ResizeObserver(() => {
                if (afWorkspace && window.Blockly && window.Blockly.svgResize) {
                    try { window.Blockly.svgResize(afWorkspace); } catch {}
                }
            });
            ro.observe(host);
        } catch {}
    }
    const B = window.Blockly;
    if (B.BlockSvg     && !B.BlockSvg.prototype._afCtxKilled)     { B.BlockSvg.prototype.showContextMenu     = function () {}; B.BlockSvg.prototype._afCtxKilled = true; }
    if (B.WorkspaceSvg && !B.WorkspaceSvg.prototype._afCtxKilled) { B.WorkspaceSvg.prototype.showContextMenu = function () {}; B.WorkspaceSvg.prototype._afCtxKilled = true; }
    if (B.ContextMenu  && !B.ContextMenu._afCtxKilled)            { B.ContextMenu.show = function () {};                       B.ContextMenu._afCtxKilled = true; }
    host.addEventListener('contextmenu', (e) => {
        e.preventDefault();
        e.stopPropagation();
        const items = typeof window.afBuildBlockContextMenu === 'function'
            ? window.afBuildBlockContextMenu(e.target) : null;
        if (typeof window.VrcnHideContextMenu === 'function') window.VrcnHideContextMenu();
        if (items && items.length && typeof window.VrcnShowContextMenu === 'function') {
            window.VrcnShowContextMenu(e.clientX, e.clientY, items);
        }
    }, true);
    const loading = document.getElementById('afLoadingHint');
    if (loading) loading.style.display = 'none';
    document.documentElement.addEventListener('themechange', afOnThemeChange);
}

/* ===========================================================================
   Conditions sidebar panel — renders below the Blockly toolbox.
   Each condition has an editable name, a value toggle, and a remove button.
   The Get/Set blocks pull their dropdown options from these names live.
   =========================================================================== */
function afRenderConditionsPanel() {
    const list = document.getElementById('afConditionsList');
    if (!list) return;
    const names = Object.keys(afConditions).sort();
    if (!names.length) {
        list.innerHTML = '<div class="af-cond-empty">' + afEsc(aft('condition.empty', 'No conditions yet. Click + to add one.')) + '</div>';
        return;
    }
    list.innerHTML = names.map(name => {
        const checked = afConditions[name] ? 'checked' : '';
        const safe    = afEsc(name);
        return ''
            + '<div class="af-cond-row" data-name="' + safe + '">'
                + '<input class="af-cond-name vrcn-input" type="text" value="' + safe + '" '
                    + 'onblur="afRenameConditionFromInput(this, \'' + jsq(name) + '\')" '
                    + 'onkeydown="if (event.key === &quot;Enter&quot;) this.blur();">'
                + '<label class="toggle af-inline-toggle">'
                    + '<input type="checkbox" ' + checked + ' onchange="afToggleConditionValue(\'' + jsq(name) + '\', this.checked)">'
                    + '<div class="toggle-track"><div class="toggle-knob"></div></div>'
                + '</label>'
                + '<button class="vrcn-button af-icon-btn af-cond-remove" onclick="afRemoveCondition(\'' + jsq(name) + '\')" title="' + afEsc(aft('common.remove', 'Remove')) + '">'
                    + '<span class="msi" style="font-size:14px;">close</span>'
                + '</button>'
            + '</div>';
    }).join('');
}

function afAddConditionPrompt() {
    vnPromptModal({
        title: aft('condition.prompt.title', 'New Condition'),
        message: esc(aft('condition.prompt.name', 'Condition name:')),
        confirmLabel: t('common.create', 'Create'),
        maxLength: 64,
        onConfirm: name => {
            if (Object.prototype.hasOwnProperty.call(afConditions, name)) {
                if (typeof showToast === 'function') showToast(false, aftf('condition.toast.already_exists', { name }, 'Condition "' + name + '" already exists'));
                return;
            }
            afConditions[name] = false;
            afScheduleConditionsSave();
            afRenderConditionsPanel();
        },
    });
}

function afRemoveCondition(name) {
    vnConfirmModal({
        title: aft('condition.confirm.title', 'Remove Condition'),
        icon: 'delete',
        message: aftf('condition.confirm.remove', { name: esc(name) }, 'Remove condition "' + esc(name) + '"?'),
        confirmLabel: t('common.remove', 'Remove'),
        onConfirm: () => {
            delete afConditions[name];
            afScheduleConditionsSave();
            afRenderConditionsPanel();
        },
    });
}

function afToggleConditionValue(name, value) {
    if (!Object.prototype.hasOwnProperty.call(afConditions, name)) return;
    afConditions[name] = !!value;
    afScheduleConditionsSave();
}

function afRenameConditionFromInput(inputEl, oldName) {
    const newName = String(inputEl.value || '').trim();
    if (!newName || newName === oldName) { inputEl.value = oldName; return; }
    if (Object.prototype.hasOwnProperty.call(afConditions, newName)) {
        if (typeof showToast === 'function') showToast(false, aftf('condition.toast.already_exists', { name: newName }, 'Condition "' + newName + '" already exists'));
        inputEl.value = oldName;
        return;
    }
    afConditions[newName] = afConditions[oldName];
    delete afConditions[oldName];
    afScheduleConditionsSave();
    afRenderConditionsPanel();
}

function afScheduleConditionsSave() {
    if (afConditionsSaveTimer) clearTimeout(afConditionsSaveTimer);
    afConditionsSaveTimer = setTimeout(() => {
        afConditionsSaveTimer = null;
        if (typeof sendToCS === 'function') sendToCS({
            action: 'afSaveConditions',
            conditions: afConditions,
        });
    }, 400);
}

window.afAddConditionPrompt        = afAddConditionPrompt;
window.afRemoveCondition           = afRemoveCondition;
window.afToggleConditionValue      = afToggleConditionValue;
window.afRenameConditionFromInput  = afRenameConditionFromInput;

function afOnThemeChange() {
    if (!afWorkspace || !window.Blockly) return;
    const cssVar = (n, f) => getComputedStyle(document.documentElement).getPropertyValue(n).trim() || f;
    try {
        const newTheme = window.Blockly.Theme.defineTheme('vrcnext-' + Date.now(), {
            base: window.Blockly.Themes.Classic,
            componentStyles: {
                workspaceBackgroundColour: cssVar('--bg-input', '#1f2330'),
                toolboxBackgroundColour:   cssVar('--bg-card',  '#2a2e3a'),
                flyoutBackgroundColour:    cssVar('--bg-card',  '#2a2e3a'),
                flyoutForegroundColour:    cssVar('--tx0',      '#ffffff'),
                scrollbarColour:           cssVar('--brd',      '#666'),
                insertionMarkerColour:     cssVar('--accent',   '#ffffff'),
                insertionMarkerOpacity:    0.5,
                cursorColour:              cssVar('--accent',   '#ffffff'),
                selectedGlowColour:        '#7dd1ff',
                selectedGlowSize:          0.8,
                replacementGlowColour:     '#7dd1ff',
                replacementGlowSize:       2,
            },
        });
        afWorkspace.setTheme(newTheme);
    } catch {}
    const host = document.getElementById('afBlocklyHost');
    if (!host) return;
    const brd = cssVar('--brd', '#3a3f4a');
    host.querySelectorAll('pattern line').forEach(line => line.setAttribute('stroke', brd));
}

function afOnWorkspaceChange(ev) {
    if (!afCurrentFlowId) return;
    if (ev.isUiEvent) return;
    if (ev.type === window.Blockly.Events.FINISHED_LOADING) {
        afUpdateActionCounter();
        return;
    }
    if (afAutoSaveSuppressed) return;

    const flow = afFlows.find(f => f.id === afCurrentFlowId);
    if (flow && afWorkspace) flow.workspace = window.Blockly.serialization.workspaces.save(afWorkspace);
    afUpdateActionCounter();
    afApplyActionLockState();
    afScheduleAutoSave();
}

const AF_COOLDOWN_TYPES = new Set(['af_cooldown', 'af_cooldown_else']);
let afCooldownVisualTimer = null;

function afCooldownIdsActive() {
    const now = Date.now();
    const ids = new Set();
    for (const entry of afCooldowns.values()) {
        if (entry.until > now && entry.blockId) ids.add(entry.blockId);
    }
    return ids;
}

function afRefreshCooldownVisuals() {
    if (!afWorkspace) {
        if (afCooldownVisualTimer) { clearInterval(afCooldownVisualTimer); afCooldownVisualTimer = null; }
        return;
    }
    const active = afCooldownIdsActive();
    for (const b of afWorkspace.getAllBlocks(false)) {
        if (!AF_COOLDOWN_TYPES.has(b.type)) continue;
        const svg = typeof b.getSvgRoot === 'function' ? b.getSvgRoot() : null;
        if (!svg) continue;
        svg.classList.toggle('af-cooldown-active', active.has(b.id));
    }
    if (active.size > 0 && !afCooldownVisualTimer) {
        afCooldownVisualTimer = setInterval(afRefreshCooldownVisuals, 500);
    } else if (active.size === 0 && afCooldownVisualTimer) {
        clearInterval(afCooldownVisualTimer);
        afCooldownVisualTimer = null;
    }
}

function afApplyActionLockState() {
    if (!afWorkspace) return;
    afRefreshCooldownVisuals();
    const overAction  = afCountActions() > FLOW_ACTION_LIMIT;
    const overTrigger = afCountGlobalTriggers() > TRIGGER_LIMIT;
    /* Task rate visual lock turns EVERY block (action + trigger) red across the
       displayed flow as a global "back off" indicator. */
    const overTaskRate = afTaskCount() >= TASK_SOFT_LIMIT;
    for (const b of afWorkspace.getAllBlocks(false)) {
        if (typeof b.getSvgRoot !== 'function') continue;
        const isAction  = ACTION_TYPES.has(b.type);
        const isTrigger = TRIGGER_TYPES.has(b.type);
        if (!isAction && !isTrigger) continue;
        const svg = b.getSvgRoot();
        if (!svg) continue;
        const locked = overTaskRate || (isAction && overAction) || (isTrigger && overTrigger);
        svg.classList.toggle('af-action-locked', locked);
        try {
            if (locked) {
                const msg = overTaskRate
                    ? aftf('warning.task_rate', { count: afTaskCount(), limit: TASK_SOFT_LIMIT }, 'API rate cap hit: ' + afTaskCount() + ' tasks in last 10min. All flows paused until the count drops below ' + TASK_SOFT_LIMIT + '.')
                    : isAction
                        ? aftf('warning.action_limit',  { limit: FLOW_ACTION_LIMIT }, 'Flow is over the ' + FLOW_ACTION_LIMIT + '-action limit. Disabled until you delete blocks.')
                        : aftf('warning.trigger_limit', { limit: TRIGGER_LIMIT },     'Over the global trigger limit (' + TRIGGER_LIMIT + '). Disabled until you delete blocks across flows.');
                b.setWarningText(msg);
            } else {
                b.setWarningText(null);
            }
        } catch {}
    }
}

function afCountActions() {
    if (!afWorkspace) return 0;
    let n = 0;
    for (const b of afWorkspace.getAllBlocks(false)) {
        if (ACTION_TYPES.has(b.type)) n++;
    }
    return n;
}

/* Rolling-window rate limit on API-calling actions. send_notification is
   excluded because it never issues a VRChat GET request. Soft limit shown
   in the badge is 20; we silently grant a 5-task grace before truly stopping. */
function afPruneTaskHistory() {
    const cutoff = Date.now() - TASK_WINDOW_MS;
    while (afTaskHistory.length && afTaskHistory[0] < cutoff) afTaskHistory.shift();
}
function afTaskCount() { afPruneTaskHistory(); return afTaskHistory.length; }
function afTryDispatch(flow) {
    afPruneTaskHistory();
    if (afTaskHistory.length >= TASK_HARD_LIMIT) {
        afLog('err', '[' + flow.name + '] ' + aftf('log.task_rate_limited', { count: afTaskHistory.length, limit: TASK_HARD_LIMIT }, 'rate limited: ' + afTaskHistory.length + '/' + TASK_HARD_LIMIT + ' tasks in last 10min'));
        return false;
    }
    return true;
}
function afRecordTask() {
    afTaskHistory.push(Date.now());
    afUpdateActionCounter();
    afApplyActionLockState();
}

function afCountActionsInWorkspace(ws) {
    if (!ws || !ws.blocks || !ws.blocks.blocks) return 0;
    let n = 0;
    const walk = (b) => {
        if (!b) return;
        if (ACTION_TYPES.has(b.type)) n++;
        if (b.inputs) for (const k in b.inputs) walk(b.inputs[k].block);
        if (b.next)   walk(b.next.block);
    };
    for (const root of ws.blocks.blocks) walk(root);
    return n;
}

function afCountTriggersInWorkspace(ws) {
    if (!ws || !ws.blocks || !ws.blocks.blocks) return 0;
    let n = 0;
    const walk = (b) => {
        if (!b) return;
        if (TRIGGER_TYPES.has(b.type)) n++;
        if (b.inputs) for (const k in b.inputs) walk(b.inputs[k].block);
        if (b.next)   walk(b.next.block);
    };
    for (const root of ws.blocks.blocks) walk(root);
    return n;
}

function afCountGlobalTriggers() {
    let n = 0;
    for (const flow of afFlows) n += afCountTriggersInWorkspace(flow.workspace);
    return n;
}

function afUpdateActionCounter() {
    const el  = document.getElementById('afActionCounterValue');
    const wrap = document.getElementById('afActionCounter');
    if (el && wrap) {
        const n = afCountActions();
        el.textContent = n + '/' + FLOW_ACTION_LIMIT;
        wrap.classList.toggle('at-limit', n >= FLOW_ACTION_LIMIT);
    }
    const tEl  = document.getElementById('afTriggerCounterValue');
    const tWrap = document.getElementById('afTriggerCounter');
    if (tEl && tWrap) {
        const g = afCountGlobalTriggers();
        tEl.textContent = g + '/' + TRIGGER_LIMIT;
        tWrap.classList.toggle('at-limit', g >= TRIGGER_LIMIT);
    }
    const kEl  = document.getElementById('afTaskCounterValue');
    const kWrap = document.getElementById('afTaskCounter');
    if (kEl && kWrap) {
        const k = afTaskCount();
        /* Show the soft limit in the badge; grace zone (21..25) is silent. */
        kEl.textContent = Math.min(k, TASK_SOFT_LIMIT) + '/' + TASK_SOFT_LIMIT;
        kWrap.classList.toggle('at-limit', k >= TASK_SOFT_LIMIT);
    }
}

function afScheduleAutoSave() {
    if (afAutoSaveTimer) clearTimeout(afAutoSaveTimer);
    afAutoSaveTimer = setTimeout(() => {
        afAutoSaveTimer = null;
        if (typeof sendToCS === 'function') sendToCS({ action: 'afSaveFlows', flows: afFlows });
    }, AUTOSAVE_DEBOUNCE_MS);
}

function afNewId() { return 'flow_' + Math.random().toString(36).slice(2, 10); }

function afNewFlow() {
    if (afFlows.length >= FLOW_LIMIT) {
        if (typeof showToast === 'function') showToast(false, aftf('toast.flow_limit_reached', { limit: FLOW_LIMIT }, 'Flow limit reached (' + FLOW_LIMIT + ' max). Delete one to create another.'));
        return;
    }
    vnPromptModal({
        title: aft('prompt.flow_name_title', 'New Flow'),
        message: esc(aft('prompt.flow_name', 'Flow name:')),
        value: aft('prompt.flow_name_default', 'New Flow'),
        confirmLabel: t('common.create', 'Create'),
        onConfirm: name => {
            const id = afNewId();
            const now = Date.now();
            afFlows.push({ id, name, enabled: false, workspace: null, createdAt: now, updatedAt: now });
            afRenderFlowSelect();
            afSelectFlow(id);
            afPersistFlows();
        },
    });
}

function afRenameFlow() {
    const flow = afFlows.find(f => f.id === afCurrentFlowId);
    if (!flow) return;
    vnPromptModal({
        title: aft('prompt.rename_title', 'Rename Flow'),
        message: esc(aft('prompt.rename', 'Rename flow:')),
        value: flow.name,
        confirmLabel: t('common.save', 'Save'),
        onConfirm: name => {
            flow.name = name;
            flow.updatedAt = Date.now();
            afRenderFlowSelect();
            afPersistFlows();
        },
    });
}

function afDeleteFlow() {
    const flow = afFlows.find(f => f.id === afCurrentFlowId);
    if (!flow) return;
    vnConfirmModal({
        title: aft('prompt.delete_title', 'Delete Flow'),
        icon: 'delete',
        message: aftf('prompt.delete_confirm', { name: esc(flow.name) }, 'Delete flow "' + esc(flow.name) + '"?'),
        confirmLabel: t('common.delete', 'Delete'),
        onConfirm: () => {
            afFlows = afFlows.filter(f => f.id !== flow.id);
            afCancelWaits();
            afResetCooldowns(flow.id);
            delete afTriggerState[flow.id];
            afCurrentFlowId = afFlows[0]?.id || null;
            afRenderFlowSelect();
            afLoadFlowIntoWorkspace(afCurrentFlowId);
            afPersistFlows();
        },
    });
}

function afSelectFlow(id) {
    if (afCurrentFlowId && afWorkspace) {
        const cur = afFlows.find(f => f.id === afCurrentFlowId);
        if (cur) cur.workspace = window.Blockly.serialization.workspaces.save(afWorkspace);
    }
    afCurrentFlowId = id;
    afLoadFlowIntoWorkspace(id);
    const sel = document.getElementById('afFlowSelect');
    if (sel) {
        sel.value = id || '';
        if (typeof sel._vnRefresh === 'function') sel._vnRefresh();
    }
    const cur = afFlows.find(f => f.id === id);
    const en = document.getElementById('afFlowEnabled');
    if (en) en.checked = !!(cur && cur.enabled);
}

function afToggleEnabled(checked) {
    const flow = afFlows.find(f => f.id === afCurrentFlowId);
    if (!flow) return;
    flow.enabled = !!checked;
    flow.updatedAt = Date.now();
    delete afTriggerState[flow.id];
    afPersistFlows();
    afUpdateRunIndicator();
    if (flow.enabled) setTimeout(afTick, 0);
}

function afToggleLogPanel() {
    const card = document.getElementById('afLogCard');
    const btn  = document.getElementById('afLogToggleBtn');
    if (!card) return;
    const visible = card.style.display !== 'none';
    card.style.display = visible ? 'none' : '';
    if (btn) btn.classList.toggle('active', !visible);
}

function afRunNow() {
    const flow = afFlows.find(f => f.id === afCurrentFlowId);
    if (!flow) { if (typeof showToast === 'function') showToast(false, aft('toast.no_flow_selected', 'No flow selected')); return; }
    if (afWorkspace) flow.workspace = window.Blockly.serialization.workspaces.save(afWorkspace);
    afCancelWaits(flow.id);
    delete afTriggerState[flow.id];
    afLog('info', '[' + flow.name + '] ' + aft('log.manual_run', 'manual run, firing all triggers'));
    const ws = flow.workspace;
    if (!ws || !ws.blocks || !ws.blocks.blocks) {
        afLog('err', '[' + flow.name + '] ' + aft('log.nothing_to_run', 'nothing to run'));
        return;
    }
    let fired = 0;
    try {
        for (const root of ws.blocks.blocks) {
            if (afIsTriggerBlock(root.type)) {
                afFireTrigger(flow, root, 'manual: ' + root.type);
                fired++;
            }
        }
    } catch (e) { afLog('err', '[' + flow.name + '] ' + (e.message || e)); }
    if (fired === 0) afLog('err', '[' + flow.name + '] ' + aft('log.no_trigger', 'no Trigger block found at root'));
}

function afSaveCurrentFlow() {
    const flow = afFlows.find(f => f.id === afCurrentFlowId);
    if (!flow) {
        if (typeof showToast === 'function') showToast(false, aft('toast.no_flow_selected', 'No flow selected'));
        return;
    }
    if (afWorkspace) flow.workspace = window.Blockly.serialization.workspaces.save(afWorkspace);
    flow.updatedAt = Date.now();
    afPersistFlows();
    if (typeof showToast === 'function') showToast(true, aft('toast.flow_saved', 'Flow saved'));
}

function afRenderFlowSelect() {
    const sel = document.getElementById('afFlowSelect');
    if (!sel) return;
    sel.innerHTML = '';
    if (!afFlows.length) {
        const opt = document.createElement('option');
        opt.value = '';
        opt.textContent = aft('no_flows', '(no flows)');
        sel.appendChild(opt);
    } else {
        afFlows.forEach(f => {
            const opt = document.createElement('option');
            opt.value = f.id;
            opt.textContent = f.name;
            opt.dataset.vnDot = f.enabled ? 'online' : 'offline';
            sel.appendChild(opt);
        });
        if (afCurrentFlowId) sel.value = afCurrentFlowId;
    }
    if (typeof initVnSelect === 'function') initVnSelect(sel);
    if (typeof sel._vnRefresh === 'function') sel._vnRefresh();
}

function afLoadFlowIntoWorkspace(id) {
    const hint = document.getElementById('afEmptyHint');
    if (!afWorkspace) return;
    afAutoSaveSuppressed = true;
    try {
        afWorkspace.clear();
        const flow = afFlows.find(f => f.id === id);
        if (!flow) {
            if (hint) hint.style.display = '';
            return;
        }
        if (hint) hint.style.display = 'none';
        if (flow.workspace && Object.keys(flow.workspace).length > 0) {
            try { window.Blockly.serialization.workspaces.load(flow.workspace, afWorkspace); }
            catch (e) { console.error('[ActionFlow] load failed', e); afLog('err', aftf('log.workspace_load_failed', { error: e.message || e }, 'Workspace load failed: ' + (e.message || e))); }
        }
    } finally {
        afPauseSvgAnimations();
        setTimeout(() => { afAutoSaveSuppressed = false; afUpdateActionCounter(); afApplyActionLockState(); }, 50);
    }
}

function afPersistFlows() {
    if (afCurrentFlowId && afWorkspace) {
        const cur = afFlows.find(f => f.id === afCurrentFlowId);
        if (cur) cur.workspace = window.Blockly.serialization.workspaces.save(afWorkspace);
    }
    afRenderFlowSelect();
    afUpdateActionCounter();
    afApplyActionLockState();
    if (typeof sendToCS === 'function') sendToCS({ action: 'afSaveFlows', flows: afFlows });
}

function afLog(level, msg) {
    afLogEntries.push({ time: new Date(), level, msg });
    while (afLogEntries.length > LOG_MAX_ENTRIES) afLogEntries.shift();
    afRenderLog();
}

function afClearLog() {
    afLogEntries = [];
    afRenderLog();
}

function afRenderLog() {
    const host = document.getElementById('afLogList');
    if (!host) return;
    if (!afLogEntries.length) {
        host.innerHTML = '<div class="af-log-empty">' + afEsc(aft('log_empty', 'No events yet. Save and enable a flow to see execution traces.')) + '</div>';
        return;
    }
    host.innerHTML = afLogEntries.slice().reverse().map(e => {
        const t = e.time.toTimeString().slice(0, 8);
        return `<div class="af-log-entry ${e.level}"><span class="af-log-time">${t}</span><span class="af-log-msg">${afEsc(e.msg)}</span></div>`;
    }).join('');
    host.scrollTop = 0;
}

function afEsc(s) { const d = document.createElement('div'); d.textContent = String(s); return d.innerHTML; }

function afStartTicker() {
    if (afTickTimer) return;
    afTickTimer = setInterval(afTick, EVENT_TICK_MS);
    afSecondTimer = setInterval(afSecondTick, 1000);
    setTimeout(afTick, 2000);
}

let afSecondTimer = null;
const afSecondState = {};

const AF_EVERY_SECOND_BLOCKED = new Set([
    'af_set_status', 'af_set_bio_text', 'af_switch_own_avatar', 'af_switch_favorite_avatar',
    'af_switch_avatar_id', 'af_set_home_world', 'af_invite_friend', 'af_request_invite',
    'af_answer_invite', 'af_answer_invite_request',
    'af_send_own_instance_info', 'af_send_own_advanced_instance_info', 'af_send_friend_instance_info',
    'af_send_webhook', 'af_send_webhook_value', 'af_send_advanced_webhook',
]);

function afCollectEverySecondBlocked(block, out) {
    let cur = block;
    while (cur) {
        if (AF_EVERY_SECOND_BLOCKED.has(cur.type) && cur.id) out.add(cur.id);
        if (cur.inputs) for (const k in cur.inputs) afCollectEverySecondBlocked(cur.inputs[k].block, out);
        cur = cur.next?.block;
    }
    return out;
}

function afSecondTick() {
    for (const flow of afFlows) {
        if (!flow.enabled || !flow.workspace) continue;
        const roots = flow.workspace.blocks?.blocks;
        if (!Array.isArray(roots)) continue;
        for (const root of roots) {
            if (root && root.type === 'af_trigger_every_second') {
                try { afFireTrigger(flow, root, 'every second'); }
                catch (e) { afLog('err', '[' + flow.name + '] ' + (e.message || e)); }
            }
        }
    }
}

const TRIGGER_TYPES = new Set([
    'af_trigger_every_second',
    'af_trigger_interval_30s',
    'af_trigger_interval_minutes',
    'af_trigger_world_change',
    'af_trigger_user_joins',
    'af_trigger_user_leaves',
    'af_trigger_user_joins_or_leaves',
    'af_trigger_own_status_change',
    'af_trigger_websocket_any',
    'af_trigger_websocket_friend',
    'af_trigger_invite_received',
    'af_trigger_invite_request_received',
    'af_trigger_manual',
    'af_trigger_time',
    'af_trigger_friend_joins_instance',
]);

function afIsTriggerBlock(type) { return TRIGGER_TYPES.has(type); }

function afTick() {
    const now      = Date.now();
    const today    = new Date(now);
    const obs = {
        now,
        hh:        today.getHours(),
        mm:        today.getMinutes(),
        dayKey:    today.toISOString().slice(0, 10),
        userIds:   afObservedInstanceUserIds(),
        users:     afObservedInstanceUsers(),
        myStatus:  (typeof currentVrcUser !== 'undefined' && currentVrcUser?.status) || null,
    };

    // Refresh public async Tasks by evaluating the Async Tasks of GetAvatarAsync and GetInstanceAsync
    // Mark ---- Build GET Current Avatar, GET Current InstanceOwner to validate IF/DO/ELSE/AND/OR Statements inside an Loop.
    // Mark ---- Do at 19.05.2026

    for (const flow of afFlows) {
        if (!flow.enabled || !flow.workspace) continue;
        try { afEvalFlow(flow, obs); }
        catch (e) { afLog('err', '[' + flow.name + '] ' + (e.message || e)); }
    }

    if (obs.userIds)  afWatchState.lastInstanceUserIds = obs.userIds;
    if (obs.users)    afWatchState.lastInstanceUsers   = obs.users;
    if (obs.myStatus) afWatchState.lastStatus          = obs.myStatus;

    afUpdateRunIndicator();
    /* Refresh Tasks badge + lock visuals so the rolling-window count drops
       back below the soft limit on its own as old timestamps expire. */
    afUpdateActionCounter();
    afApplyActionLockState();
}

function afObservedInstanceUserIds() {
    if (typeof currentInstanceData === 'undefined' || !currentInstanceData) return null;
    if (currentInstanceData.empty || currentInstanceData.error) return null;
    const arr = Array.isArray(currentInstanceData.users) ? currentInstanceData.users : [];
    return new Set(arr.map(u => u && u.id).filter(Boolean));
}

function afObservedInstanceUsers() {
    if (typeof currentInstanceData === 'undefined' || !currentInstanceData) return null;
    if (currentInstanceData.empty || currentInstanceData.error) return null;
    const arr = Array.isArray(currentInstanceData.users) ? currentInstanceData.users : [];
    const map = new Map();
    for (const u of arr) if (u && u.id) map.set(u.id, u);
    return map;
}

function afUpdateRunIndicator() {
    const dot = document.getElementById('afRunDot');
    const txt = document.getElementById('afRunText');
    if (!dot || !txt) return;
    const anyEnabled = afFlows.some(f => f.enabled);
    if (anyEnabled) { dot.className = 'sf-dot online';  txt.textContent = aft('status.running', 'Running'); }
    else            { dot.className = 'sf-dot offline'; txt.textContent = aft('status.idle',    'Idle');    }
}

function afEvalFlow(flow, obs) {
    const ws = flow.workspace;
    if (!ws || !ws.blocks || !ws.blocks.blocks) return;
    for (const root of ws.blocks.blocks) {
        afExecRootBlock(flow, root, obs);
    }
}

function afExecRootBlock(flow, block, obs) {
    if (!block) return;
    if (afIsTriggerBlock(block.type)) afEvalTrigger(flow, block, obs);
    if (block.next && block.next.block) afExecRootBlock(flow, block.next.block, obs);
}

function afEvalTrigger(flow, block, obs) {
    const state = afTriggerState[flow.id] = afTriggerState[flow.id] || {};
    const ts    = state[block.id] = state[block.id] || {};
    const f     = block.fields || {};

    switch (block.type) {
        case 'af_trigger_interval_30s': {
            if (!ts.lastFiredMs || (obs.now - ts.lastFiredMs) >= 30 * 1000) {
                ts.lastFiredMs = obs.now;
                afFireTrigger(flow, block, 'every 30s');
            }
            return;
        }
        case 'af_trigger_interval_minutes': {
            const min = Math.max(1, Number(f.MIN || 1));
            const ms  = min * 60 * 1000;
            if (!ts.lastFiredMs || (obs.now - ts.lastFiredMs) >= ms) {
                ts.lastFiredMs = obs.now;
                afFireTrigger(flow, block, 'every ' + min + ' min');
            }
            return;
        }
        case 'af_trigger_time': {
            let hh = Number(f.HH);
            if (f.AMPM === 'PM' && hh < 12) hh += 12;
            if (f.AMPM === 'AM' && hh === 12) hh = 0;
            const mm = Number(f.MM);
            if (obs.hh === hh && obs.mm === mm && ts.lastFiredDay !== obs.dayKey) {
                ts.lastFiredDay = obs.dayKey;
                afFireTrigger(flow, block, 'at ' + hh + ':' + String(mm).padStart(2, '0'));
            }
            return;
        }
        case 'af_trigger_world_change':
            return;
        case 'af_trigger_user_joins':
        case 'af_trigger_user_leaves':
        case 'af_trigger_user_joins_or_leaves': {
            if (!obs.userIds || !afWatchState.lastInstanceUserIds) return;
            const fireJoins  = block.type !== 'af_trigger_user_leaves';
            const fireLeaves = block.type !== 'af_trigger_user_joins';
            const filterOn = (f.FILTER === 'TRUE' || f.FILTER === true);
            const filterId = filterOn ? String(f.USER_ID || '').trim() : '';
            const matches = (id) => !filterId || id === filterId;
            if (fireJoins) {
                for (const id of obs.userIds) {
                    if (!afWatchState.lastInstanceUserIds.has(id) && matches(id)) {
                        afFireTrigger(flow, block, 'user joined: ' + id, afLookupUser(id), null, 'join');
                    }
                }
            }
            if (fireLeaves) {
                for (const id of afWatchState.lastInstanceUserIds) {
                    if (!obs.userIds.has(id) && matches(id)) {
                        afFireTrigger(flow, block, 'user left: ' + id, afLookupUser(id), null, 'leave');
                    }
                }
            }
            return;
        }
        case 'af_trigger_own_status_change': {
            if (!obs.myStatus || !afWatchState.lastStatus) return;
            if (obs.myStatus !== afWatchState.lastStatus) {
                afFireTrigger(flow, block, 'own status: ' + afWatchState.lastStatus + ' → ' + obs.myStatus);
            }
            return;
        }
        case 'af_trigger_websocket_any':
        case 'af_trigger_websocket_friend':
        case 'af_trigger_invite_received':
        case 'af_trigger_invite_request_received':
        case 'af_trigger_friend_joins_instance':
        case 'af_trigger_manual':
            return;
    }
}

function afFireTrigger(flow, block, reason, triggeringUser, notificationId, triggerAction) {
    const actionCount = afCountActionsInWorkspace(flow.workspace);
    if (actionCount > FLOW_ACTION_LIMIT) {
        afLog('err', '[' + flow.name + '] ' + aftf('log.over_action_limit', { count: actionCount, limit: FLOW_ACTION_LIMIT }, 'over action limit (' + actionCount + '/' + FLOW_ACTION_LIMIT + '). Flow disabled until trimmed.'));
        return;
    }
    const triggerCount = afCountGlobalTriggers();
    if (triggerCount > TRIGGER_LIMIT) {
        afLog('err', '[' + flow.name + '] ' + aftf('log.over_trigger_limit', { count: triggerCount, limit: TRIGGER_LIMIT }, 'over global trigger limit (' + triggerCount + '/' + TRIGGER_LIMIT + '). All triggers disabled until trimmed.'));
        return;
    }
    const prevCtx = afContext;
    const triggerKind =
        block.type === 'af_trigger_invite_received'         ? 'invite' :
        block.type === 'af_trigger_invite_request_received' ? 'requestInvite' : null;
    afContext = {
        triggeringUser: triggeringUser || null,
        triggerKind,
        notificationId: notificationId || null,
        triggerAction: triggerAction || null,
    };
    try {
        if (block.type === 'af_trigger_every_second') {
            const st = afSecondState[flow.id] = afSecondState[flow.id] || { warned: new Set() };
            st.blocked = afCollectEverySecondBlocked(afInputStatement(block, 'DO'), new Set());
        } else {
            afLog('info', '[' + flow.name + '] ' + aftf('log.trigger_fired', { reason }, 'trigger fired (' + reason + ')'));
        }
        afExecStatements(flow, afInputStatement(block, 'DO'));
    } finally {
        afContext = prevCtx;
    }
}

function afInstanceUserName(id) {
    if (!id) return '';
    const u = afObservedInstanceUsers()?.get(id) || afWatchState.lastInstanceUsers?.get(id);
    return u?.displayName || '';
}

function afLookupUser(id) {
    if (!id) return null;
    const live = typeof vrcFriendsData !== 'undefined' && vrcFriendsData.find(x => x.id === id);
    if (live) return live;

    const inst = afObservedInstanceUsers()?.get(id) || afWatchState.lastInstanceUsers?.get(id);
    if (inst && inst.displayName) return inst;

    return { id };
}

let _afFriendJoinDebounce = {};
let _afFriendJoinFired = {};
const AF_FRIEND_JOIN_DEBOUNCE_MS = 3000;
const AF_FRIEND_JOIN_COOLDOWN_MS = 15000;

function afFireFriendJoinInstance(payload) {
    const friendId = payload.friendId || '';
    const triggeringFriend = {
        id: friendId,
        displayName: payload.friendName || friendId,
        location: payload.location || '',
        _worldId: payload.worldId || '',
        _worldName: payload.worldName || '',
        _worldImage: payload.worldThumb || '',
        _friendImage: payload.friendImage || '',
    };
    for (const flow of afFlows) {
        if (!flow.enabled || !flow.workspace?.blocks?.blocks) continue;
        for (const root of flow.workspace.blocks.blocks) {
            if (root.type !== 'af_trigger_friend_joins_instance') continue;
            const want = root.fields?.FRIEND_ID;
            if (want && want !== friendId) continue;
            afFireTrigger(flow, root, 'friend joined instance: ' + (payload.friendName || friendId), triggeringFriend);
        }
    }
}

window.__afOnWebsocketEvent = function (type, payload) {
    if (type === 'friendTimelineEvent' && payload && typeof payload === 'object') {
        const evType = payload.type || '';
        const loc    = payload.location || '';
        const joined = (evType === 'friend_gps' || evType === 'friend_online')
            && loc && loc !== 'private' && loc !== 'offline' && loc !== 'traveling';
        if (joined) {
            const key = (payload.friendId || '') + '|' + loc;
            const firedAt = _afFriendJoinFired[key];
            if (firedAt && (Date.now() - firedAt) < AF_FRIEND_JOIN_COOLDOWN_MS) return;
            const existing = _afFriendJoinDebounce[key];
            if (existing) {
                existing.payload = payload;
            } else {
                const entry = { payload };
                entry.timer = setTimeout(() => {
                    delete _afFriendJoinDebounce[key];
                    _afFriendJoinFired[key] = Date.now();
                    try { afFireFriendJoinInstance(entry.payload); } catch (e) { console.error('[ActionFlow] friend join', e); }
                }, AF_FRIEND_JOIN_DEBOUNCE_MS);
                _afFriendJoinDebounce[key] = entry;
            }
        }
        return;
    }
    if (!type || !type.startsWith('vrc')) return;
    if (type === 'vrcFriends' || type === 'vrcCredits') return;

    if (type === 'vrcWorldJoined') {
        const worldId = payload?.worldId || '';
        setTimeout(() => {
            for (const flow of afFlows) {
                if (!flow.enabled || !flow.workspace?.blocks?.blocks) continue;
                for (const root of flow.workspace.blocks.blocks) {
                    if (root.type === 'af_trigger_world_change') {
                        afFireTrigger(flow, root, 'world joined: ' + worldId);
                    }
                }
            }
        }, WORLD_CHANGE_DELAY_MS);
    }

    if (type === 'vrcNotificationPrepend' && payload && typeof payload === 'object') {
        const notifType = payload.type;
        const senderId  = payload.senderUserId || null;
        const notifId   = payload.id || null;
        const sender    = senderId ? afLookupUser(senderId) : null;
        const targetTrigger =
            notifType === 'invite'        ? 'af_trigger_invite_received' :
            notifType === 'requestInvite' ? 'af_trigger_invite_request_received' : null;
        if (targetTrigger) {
            for (const flow of afFlows) {
                if (!flow.enabled || !flow.workspace?.blocks?.blocks) continue;
                for (const root of flow.workspace.blocks.blocks) {
                    if (root.type === targetTrigger) {
                        afFireTrigger(flow, root, notifType + ' from ' + (senderId || 'unknown'), sender, notifId);
                    }
                }
            }
        }
    }

    const userId = afExtractUserId(payload);

    for (const flow of afFlows) {
        if (!flow.enabled || !flow.workspace?.blocks?.blocks) continue;
        for (const root of flow.workspace.blocks.blocks) {
            if (!afIsTriggerBlock(root.type)) continue;
            if (root.type === 'af_trigger_websocket_any') {
                afFireTrigger(flow, root, 'ws: ' + type, userId ? afLookupUser(userId) : null);
            } else if (root.type === 'af_trigger_websocket_friend') {
                const want = root.fields?.FRIEND_ID;
                if (want && userId && want === userId) {
                    afFireTrigger(flow, root, 'ws for ' + want + ': ' + type, afLookupUser(userId));
                }
            }
        }
    }
};

function afExtractUserId(payload) {
    if (!payload || typeof payload !== 'object') return null;
    return payload.userId || payload.id || payload.senderUserId || (payload.user && payload.user.id) || null;
}

const AF_WAIT_POLL_MS   = 1000;
const AF_WAIT_TIMEOUT_MS = 30 * 60 * 1000;
const AF_WAIT_MAX_SECS   = 3600;
const afWaitPending = new Map();
const afCooldowns = new Map();

function afCancelWaits(flowId) {
    for (const [block, handle] of afWaitPending) {
        if (flowId && handle.flowId !== flowId) continue;
        clearTimeout(handle.timer);
        clearInterval(handle.timer);
        afWaitPending.delete(block);
    }
}

function afCooldownKey(flow, block) {
    return flow.id + ':' + (block.id || '');
}

function afResetCooldowns(flowId) {
    for (const [key, entry] of afCooldowns) {
        if (flowId && entry.flowId !== flowId) continue;
        afCooldowns.delete(key);
    }
}

function afWaitFlowGone(flow, wasEnabled) {
    const live = afFlows.find(x => x.id === flow.id);
    if (!live) return true;
    return wasEnabled && !live.enabled;
}

function afRunWaitBranch(flow, block, ctx, wasEnabled) {
    afWaitPending.delete(block);
    if (afWaitFlowGone(flow, wasEnabled)) return;
    const live = afFlows.find(x => x.id === flow.id) || flow;
    const prevCtx = afContext;
    afContext = ctx;
    try { afExecStatements(live, afInputStatement(block, 'DO')); }
    finally { afContext = prevCtx; }
}

function afStartWaitFor(flow, block, secs) {
    const ctx = { ...afContext };
    const wasEnabled = !!flow.enabled;
    const timer = setTimeout(() => afRunWaitBranch(flow, block, ctx, wasEnabled), secs * 1000);
    afWaitPending.set(block, { timer, flowId: flow.id });
}

function afWaitUntilMatches(block) {
    const want = afInput(block, 'STATE') ? !!afEvalValue(afInput(block, 'STATE')) : true;
    return !!afEvalValue(afInput(block, 'COND')) === want;
}

function afStartWaitUntil(flow, block) {
    const ctx = { ...afContext };
    const wasEnabled = !!flow.enabled;
    const startedAt = Date.now();
    const timer = setInterval(() => {
        if (Date.now() - startedAt > AF_WAIT_TIMEOUT_MS) {
            clearInterval(timer);
            afWaitPending.delete(block);
            afLog('err', '[' + flow.name + '] ' + aft('log.wait_timeout', 'wait until gave up: condition stayed false'));
            return;
        }
        if (afWaitFlowGone(flow, wasEnabled)) {
            clearInterval(timer);
            afWaitPending.delete(block);
            return;
        }
        let ok = false;
        const prevCtx = afContext;
        afContext = ctx;
        try { ok = afWaitUntilMatches(block); }
        catch { ok = false; }
        finally { afContext = prevCtx; }
        if (!ok) return;
        clearInterval(timer);
        afRunWaitBranch(flow, block, ctx, wasEnabled);
    }, AF_WAIT_POLL_MS);
    afWaitPending.set(block, { timer, flowId: flow.id });
}

function afExecStatements(flow, block) {
    let cur = block;
    while (cur) {
        afExecAction(flow, cur);
        cur = cur.next?.block;
    }
}

function afInstanceTypeLabel(internalType) {
    if (typeof getInstanceBadge === 'function') {
        const b = getInstanceBadge(internalType);
        if (b && b.label) return b.label;
    }
    return internalType || '';
}

function afWorldNameFor(worldId, fallbackName) {
    let name = fallbackName || '';
    if (!name && worldId && typeof worldInfoCache !== 'undefined' && worldInfoCache[worldId])
        name = worldInfoCache[worldId].name || '';
    return name || worldId || '';
}

function afOwnInstanceInfo() {
    const ci = (typeof currentInstanceData !== 'undefined') ? currentInstanceData : null;
    if (!ci || ci.empty || ci.error || !ci.location) return null;
    const worldId   = String(ci.location).split(':')[0];
    const raw       = String(ci.instanceType || '');
    const typeLabel = afInstanceTypeLabel(raw === 'hidden' ? 'friends+' : raw === 'group-members' ? 'group' : raw);
    return { worldId, worldName: afWorldNameFor(worldId, ci.worldName), typeLabel, capacity: Number(ci.capacity) || 0, location: String(ci.location) };
}

function afFriendInstanceInfo(friend) {
    if (!friend) return null;
    const loc = friend.location || '';
    if (!loc || loc === 'private' || loc === 'offline' || loc === 'traveling') return null;
    const parsed = (typeof parseFriendLocation === 'function')
        ? parseFriendLocation(loc)
        : { worldId: String(loc).split(':')[0], instanceType: 'public' };
    const worldId = friend._worldId || parsed.worldId;
    return {
        friendName: friend.displayName || friend.id,
        worldId,
        worldName: afWorldNameFor(worldId, friend._worldName),
        typeLabel: afInstanceTypeLabel(parsed.instanceType),
        location: loc,
    };
}

function afExecAction(flow, block) {
    const f = block.fields || {};
    const secondSt = afSecondState[flow.id];
    if (secondSt && secondSt.blocked && block.id && secondSt.blocked.has(block.id)) {
        if (!secondSt.warned.has(block.id)) {
            secondSt.warned.add(block.id);
            afLog('err', '[' + flow.name + '] ' + aftf('log.every_second_blocked', { type: block.type }, 'skipped ' + block.type + ': "do every second" does not run VRC Actions or Webhook Actions'));
        }
        return;
    }
    switch (block.type) {
        case 'af_if': {
            if (afEvalValue(afInput(block, 'IF0'))) afExecStatements(flow, afInputStatement(block, 'DO0'));
            return;
        }
        case 'af_if_else': {
            const branch = afEvalValue(afInput(block, 'IF0')) ? 'DO0' : 'ELSE';
            afExecStatements(flow, afInputStatement(block, branch));
            return;
        }
        case 'af_wait_for': {
            if (afWaitPending.has(block)) {
                afLog('info', '[' + flow.name + '] ' + aft('log.wait_busy', 'wait skipped: this block is already waiting'));
                return;
            }
            const secs = Math.max(0, Math.min(AF_WAIT_MAX_SECS, Number(afEvalValue(afInput(block, 'SECS'))) || 0));
            if (!afInputStatement(block, 'DO')) return;
            afStartWaitFor(flow, block, secs);
            afLog('ok', '[' + flow.name + '] ' + aftf('log.wait_started', { secs: String(secs) }, 'waiting ' + secs + 's'));
            return;
        }
        case 'af_cooldown':
        case 'af_cooldown_else': {
            const branch = afInputStatement(block, 'DO');
            const elseBranch = afInputStatement(block, 'ELSE');
            if (!branch && !elseBranch) return;
            const cdSecs = Math.max(0, Math.min(AF_WAIT_MAX_SECS, Number(afEvalValue(afInput(block, 'SECS'))) || 0));
            const cdKey = afCooldownKey(flow, block);
            const prev = afCooldowns.get(cdKey);
            const now = Date.now();
            if (prev && now - prev.at < cdSecs * 1000) {
                const left = Math.ceil((cdSecs * 1000 - (now - prev.at)) / 1000);
                afLog('info', '[' + flow.name + '] ' + aftf('log.cooldown_active', { secs: String(left) }, 'cooldown active, ' + left + 's left'));
                if (elseBranch) afExecStatements(flow, elseBranch);
                return;
            }
            if (!branch) return;
            afCooldowns.set(cdKey, { at: now, flowId: flow.id, until: now + cdSecs * 1000, blockId: block.id || '' });
            afRefreshCooldownVisuals();
            afExecStatements(flow, branch);
            return;
        }
        case 'af_wait_until': {
            if (afWaitPending.has(block)) {
                afLog('info', '[' + flow.name + '] ' + aft('log.wait_busy', 'wait skipped: this block is already waiting'));
                return;
            }
            if (!afInputStatement(block, 'DO')) return;
            if (afWaitUntilMatches(block)) {
                afExecStatements(flow, afInputStatement(block, 'DO'));
                return;
            }
            afStartWaitUntil(flow, block);
            afLog('ok', '[' + flow.name + '] ' + aft('log.wait_until_started', 'waiting until the condition becomes true'));
            return;
        }
        case 'af_set_condition': {
            const name  = f.NAME;
            const value = f.VALUE === 'TRUE';
            if (!name) { afLog('err', '[' + flow.name + '] ' + aft('log.set_condition_skipped', 'set condition skipped: no name')); return; }
            if (!Object.prototype.hasOwnProperty.call(afConditions, name)) {
                afLog('err', '[' + flow.name + '] ' + aftf('log.set_condition_unknown', { name }, 'set condition skipped: "' + name + '" not defined (add it in the Conditions sidebar)'));
                return;
            }
            afConditions[name] = value;
            afScheduleConditionsSave();
            afRenderConditionsPanel();
            afLog('ok', '[' + flow.name + '] ' + aftf('log.set_condition', { name, value: value ? 'true' : 'false' }, 'set condition "' + name + '" = ' + value));
            return;
        }
        case 'af_set_status': {
            if (!afTryDispatch(flow)) break;
            const status = f.STATUS || 'active';
            const text   = String(f.TEXT || '');
            const desc = text || ((typeof currentVrcUser !== 'undefined' && currentVrcUser?.statusDescription) || '');
            if (typeof sendToCS === 'function') sendToCS({ action: 'vrcUpdateStatus', status, statusDescription: desc });
            afRecordTask();
            afLog('ok', '[' + flow.name + '] ' + (text
                ? aftf('log.set_status_with_text', { status: vrcStatusLabel(status), text }, 'set status to ' + vrcStatusLabel(status) + ' / "' + text + '"')
                : aftf('log.set_status',           { status: vrcStatusLabel(status) },       'set status to ' + vrcStatusLabel(status))));
            break;
        }
        case 'af_set_bio_text': {
            if (!afTryDispatch(flow)) break;
            const text = f.TEXT || '';
            if (typeof sendToCS === 'function') sendToCS({ action: 'vrcUpdateProfile', bio: text });
            afRecordTask();
            afLog('ok', '[' + flow.name + '] ' + aftf('log.set_bio', { text }, 'set bio to "' + text + '"'));
            break;
        }
        case 'af_invite_friend': {
            const user = afEvalUser(afInput(block, 'USER'));
            if (!user || !user.id) { afLog('err', '[' + flow.name + '] ' + aft('log.invite_skipped', 'invite skipped: missing user')); break; }
            if (!afTryDispatch(flow)) break;
            if (typeof sendToCS === 'function') sendToCS({ action: 'vrcInviteFriend', userId: user.id });
            afRecordTask();
            afLog('ok', '[' + flow.name + '] ' + aftf('log.invite_sent', { target: user.displayName || user.id }, 'invite sent to ' + (user.displayName || user.id)));
            break;
        }
        case 'af_request_invite': {
            const user = afEvalUser(afInput(block, 'USER'));
            if (!user || !user.id) { afLog('err', '[' + flow.name + '] ' + aft('log.request_invite_skipped', 'request invite skipped: missing user')); break; }
            if (!afTryDispatch(flow)) break;
            if (typeof sendToCS === 'function') sendToCS({ action: 'vrcRequestInvite', userId: user.id });
            afRecordTask();
            afLog('ok', '[' + flow.name + '] ' + aftf('log.request_invite_sent', { target: user.displayName || user.id }, 'request invite from ' + (user.displayName || user.id)));
            break;
        }
        case 'af_send_notification': {
            /* Exempt from rate limit: no VRChat API call, just local UI + tray. */
            const text = f.TEXT || '';
            afShowFlowNotificationCard(flow.name, text);
            if (typeof sendToCS === 'function') sendToCS({ action: 'afTrayNotify', title: flow.name, subtitle: text, accent: 'info' });
            afLog('ok', '[' + flow.name + '] ' + aftf('log.notify', { text }, 'notify "' + text + '"'));
            break;
        }
        case 'af_send_notification_value':
        case 'af_send_advanced_notification': {
            const raw = afEvalValue(afInput(block, 'VALUE'));
            const val = (raw === null || raw === undefined) ? ''
                      : afValueText(raw);
            const lead = block.type === 'af_send_advanced_notification' ? String(f.TEXT || '') : '';
            const text = (lead ? lead + ' ' + val : val).trim();
            const icon = afFindIcon(afInput(block, 'VALUE'));
            if (!text && !icon) { afLog('err', '[' + flow.name + '] ' + aft('log.notify_skipped', 'notification skipped: nothing to send')); break; }
            afShowFlowNotificationCard(flow.name, text, icon);
            if (typeof sendToCS === 'function') sendToCS({ action: 'afTrayNotify', title: flow.name, subtitle: text, accent: 'info', imageUrl: icon ? icon.url : '', friendId: icon ? icon.id : '' });
            afLog('ok', '[' + flow.name + '] ' + aftf('log.notify', { text }, 'notify "' + text + '"'));
            break;
        }
        case 'af_send_webhook':
        case 'af_send_webhook_value':
        case 'af_send_advanced_webhook': {
            const url = String(f.WEBHOOK || '').trim();
            if (!/^https:\/\//i.test(url)) { afLog('err', '[' + flow.name + '] ' + aft('log.webhook_missing_text', 'webhook skipped: missing or invalid webhook url')); break; }
            let text = String(f.TEXT || '');
            if (block.type !== 'af_send_webhook') {
                const raw = afEvalValue(afInput(block, 'VALUE'));
                const val = (raw === null || raw === undefined) ? ''
                          : afValueText(raw);
                const lead = block.type === 'af_send_advanced_webhook' ? text : '';
                text = (lead ? lead + ' ' + val : val).trim();
            }
            const hookIcon = afFindIcon(afInput(block, 'VALUE'));
            if (!text && !hookIcon) { afLog('err', '[' + flow.name + '] ' + aft('log.webhook_skipped', 'webhook skipped: nothing to send')); break; }
            if (typeof sendToCS === 'function') sendToCS({
                action: 'afTextWebhook', url, text, flow: flow.name,
                iconUserId: hookIcon ? hookIcon.id : '', iconUrl: hookIcon ? hookIcon.url : '',
            });
            afLog('ok', '[' + flow.name + '] ' + aftf('log.webhook_sent', { text }, 'sent to webhook "' + text + '"'));
            break;
        }
        case 'af_set_feature': {
            const featId = String(f.FEATURE || '');
            const stateIn = afInput(block, 'STATE');
            const want   = stateIn ? !!afEvalValue(stateIn) : String(f.STATE || 'true') === 'true';
            const entry  = AF_FEATURES.find(x => x[0] === featId);
            if (!entry) { afLog('err', '[' + flow.name + '] ' + aft('log.feature_unknown', 'set feature skipped: unknown feature')); break; }
            const featLabel = afFeatureLabel(entry[0], entry[1]);
            const featState = aft(want ? 'block.true' : 'block.false', want ? 'true' : 'false');
            let isOn;
            try { isOn = !!entry[2](); }
            catch {
                afLog('err', '[' + flow.name + '] ' + aftf('log.feature_unavailable', { feature: featLabel }, 'set feature skipped: ' + featLabel + ' is not available'));
                break;
            }
            if (isOn === want) {
                afLog('ok', '[' + flow.name + '] ' + aftf('log.feature_unchanged', { feature: featLabel, state: featState }, featLabel + ' already ' + featState));
                break;
            }
            try { (want ? entry[3] : entry[4])(); }
            catch {
                afLog('err', '[' + flow.name + '] ' + aftf('log.feature_unavailable', { feature: featLabel }, 'set feature skipped: ' + featLabel + ' is not available'));
                break;
            }
            afLog('ok', '[' + flow.name + '] ' + aftf('log.feature_set', { feature: featLabel, state: featState }, 'set ' + featLabel + ' to ' + featState));
            break;
        }
        case 'af_osc_set_bool':
        case 'af_osc_set_float':
        case 'af_osc_set_int': {
            const oscPath = String(f.PARAM || '').trim();
            if (!oscPath) { afLog('err', '[' + flow.name + '] ' + aft('log.osc_no_param', 'OSC send skipped: no parameter set')); break; }
            const oscRaw  = afEvalValue(afInput(block, 'VALUE'));
            const oscType = block.type === 'af_osc_set_bool' ? 'bool'
                          : block.type === 'af_osc_set_float' ? 'float' : 'int';
            const isRawAddress = oscPath.startsWith('/');
            let oscValue;
            if (oscType === 'bool') oscValue = !!oscRaw;
            else if (oscType === 'float') {
                oscValue = Number(oscRaw) || 0;
                if (!isRawAddress) oscValue = Math.max(-1, Math.min(1, oscValue));
            } else {
                oscValue = Math.trunc(Number(oscRaw) || 0);
                if (!isRawAddress) oscValue = Math.max(0, Math.min(255, oscValue));
            }
            if (typeof sendToCS === 'function') {
                if (isRawAddress) sendToCS({ action: 'oscSendRaw', address: oscPath, type: oscType, value: oscValue });
                else sendToCS({ action: 'oscSend', name: oscPath, type: oscType, value: oscValue });
            }
            afLog('ok', '[' + flow.name + '] ' + aftf('log.osc_sent', { param: oscPath, value: String(oscValue) }, 'OSC ' + oscPath + ' = ' + oscValue));
            break;
        }
        case 'af_close_vrchat': {
            if (typeof sendToCS === 'function') sendToCS({ action: 'afCloseVrchat' });
            afLog('ok', '[' + flow.name + '] ' + aft('log.close_vrchat', 'closing VRChat'));
            break;
        }
        case 'af_switch_avatar_id': {
            const avId = String(f.AVATAR_ID || '').trim();
            if (!avId.startsWith('avtr_')) { afLog('err', '[' + flow.name + '] ' + aft('log.switch_avatar_id_skipped', 'switch avatar skipped: invalid avatar ID')); break; }
            if (!afTryDispatch(flow)) break;
            if (typeof sendToCS === 'function') sendToCS({ action: 'vrcSelectAvatar', avatarId: avId });
            afRecordTask();
            afLog('ok', '[' + flow.name + '] ' + aftf('log.switch_avatar', { name: avId }, 'switch avatar to ' + avId));
            break;
        }
        case 'af_set_home_world': {
            const ciHome = afCurInst();
            const homeWid = ciHome ? String(ciHome.worldId || String(ciHome.location || '').split(':')[0] || '') : '';
            if (!homeWid.startsWith('wrld_')) { afLog('err', '[' + flow.name + '] ' + aft('log.set_home_world_skipped', 'set home world skipped: not currently in a world')); break; }
            if (!afTryDispatch(flow)) break;
            if (typeof sendToCS === 'function') sendToCS({ action: 'vrcSetHomeWorld', worldId: homeWid });
            afRecordTask();
            afLog('ok', '[' + flow.name + '] ' + aftf('log.set_home_world', { world: (ciHome && ciHome.worldName) || homeWid }, 'set home world to ' + ((ciHome && ciHome.worldName) || homeWid)));
            break;
        }
        case 'af_switch_own_avatar':
        case 'af_switch_favorite_avatar': {
            const avatarId = f.AVATAR_ID;
            if (!avatarId) { afLog('err', '[' + flow.name + '] ' + aft('log.switch_avatar_skipped', 'switch avatar skipped: no avatar selected')); break; }
            if (!afTryDispatch(flow)) break;
            const pool = block.type === 'af_switch_favorite_avatar'
                ? (typeof favAvatarsData !== 'undefined' ? favAvatarsData : [])
                : (typeof avatarsData    !== 'undefined' ? avatarsData    : []);
            const meta = pool.find(a => a.id === avatarId);
            const label = (meta && meta.name) || avatarId;
            if (typeof sendToCS === 'function') sendToCS({ action: 'vrcSelectAvatar', avatarId });
            afRecordTask();
            afLog('ok', '[' + flow.name + '] ' + aftf('log.switch_avatar', { name: label }, 'switch avatar to ' + label));
            break;
        }
        case 'af_answer_invite':
        case 'af_answer_invite_request': {
            const text = f.TEXT || '';
            const target = afContext.triggeringUser;
            const notifId = afContext.notificationId;
            const notifType = afContext.triggerKind;
            if (!target || !target.id || !notifId || !notifType) {
                afLog('err', '[' + flow.name + '] ' + aftf('log.reply_skipped', { action: block.type }, block.type + ' skipped: no triggering notification (use inside a "when someone invites me" or "requests invite" trigger)'));
                break;
            }
            if (!afTryDispatch(flow)) break;
            if (typeof sendToCS === 'function') sendToCS({ action: 'afSendChatMessage', userId: target.id, text, notificationId: notifId, notifType });
            afRecordTask();
            afLog('ok', '[' + flow.name + '] ' + aftf('log.reply_sent', { target: target.displayName || target.id, text }, 'reply to ' + (target.displayName || target.id) + ': "' + text + '"'));
            break;
        }
        case 'af_send_own_instance_info':
        case 'af_send_own_advanced_instance_info': {
            const url = String(f.WEBHOOK || '').trim();
            if (!/^https:\/\//i.test(url)) { afLog('err', '[' + flow.name + '] ' + aft('log.webhook_missing', 'instance info skipped: missing or invalid webhook url')); break; }
            const info = afOwnInstanceInfo();
            if (!info) { afLog('err', '[' + flow.name + '] ' + aft('log.no_own_instance', 'instance info skipped: not currently in an instance')); break; }
            const advanced = block.type === 'af_send_own_advanced_instance_info';
            const me = (typeof currentVrcUser !== 'undefined' && currentVrcUser) ? currentVrcUser : null;
            const meRaw = (me && me.rawJson) ? me.rawJson : (me || {});
            const meIcon = meRaw.iconUrl || meRaw.userIcon || meRaw.profilePicOverride
                || meRaw.currentAvatarThumbnailImageUrl || meRaw.currentAvatarImageUrl || '';
            if (typeof sendToCS === 'function') sendToCS({
                action: 'afInstanceWebhook', url, scope: 'own', advanced,
                worldId: info.worldId, worldName: info.worldName, location: info.location,
                instanceTypeLabel: info.typeLabel, capacity: info.capacity,
                authorName: me ? (me.displayName || me.username || '') : '',
                authorUserId: me ? (me.id || '') : '',
                authorIconUrl: meIcon,
            });
            afLog('ok', '[' + flow.name + '] ' + aftf('log.sent_own_instance_info', { world: info.worldName }, 'sent own instance info (' + info.worldName + ')'));
            break;
        }
        case 'af_send_friend_instance_info': {
            const url = String(f.WEBHOOK || '').trim();
            if (!/^https:\/\//i.test(url)) { afLog('err', '[' + flow.name + '] ' + aft('log.webhook_missing', 'instance info skipped: missing or invalid webhook url')); break; }
            const wantId = String(f.FRIEND_ID || '').trim();
            let friend = wantId
                ? ((typeof vrcFriendsData !== 'undefined' && vrcFriendsData.find(x => x.id === wantId)) || afContext.triggeringUser)
                : afContext.triggeringUser;
            const info = afFriendInstanceInfo(friend);
            if (!info) { afLog('err', '[' + flow.name + '] ' + aft('log.no_friend_instance', 'friend instance info skipped: no friend in an instance (use inside a "when a friend joins an instance" trigger, or pick a friend)')); break; }
            if (typeof sendToCS === 'function') sendToCS({
                action: 'afInstanceWebhook', url, scope: 'friend',
                worldId: info.worldId, worldName: info.worldName, location: info.location,
                instanceTypeLabel: info.typeLabel,
                authorName: info.friendName,
                authorUserId: (friend && friend.id) || '',
                authorIconUrl: (friend && (friend.image || friend._friendImage)) || '',
            });
            afLog('ok', '[' + flow.name + '] ' + aftf('log.sent_friend_instance_info', { friend: info.friendName }, 'sent friend instance info (' + info.friendName + ')'));
            break;
        }
        default:
            afLog('err', '[' + flow.name + '] ' + aftf('log.unknown_action', { type: block.type }, 'unknown action ' + block.type));
    }
}

function afInput(block, name) {
    return block.inputs && block.inputs[name] && block.inputs[name].block;
}
function afInputStatement(block, name) {
    return block.inputs && block.inputs[name] && block.inputs[name].block;
}

const AF_ICON_TYPES = new Set(['af_friend_icon', 'af_get_joined_player_image', 'af_get_left_player_image']);

function afFindIcon(block, depth) {
    if (!block || (depth || 0) > 6) return null;
    if (AF_ICON_TYPES.has(block.type)) {
        const val = afEvalValue(block);
        return (val && val.__afIcon) ? val : null;
    }
    const inputs = block.inputs || {};
    for (const name of Object.keys(inputs)) {
        const child = inputs[name] && inputs[name].block;
        const hit = afFindIcon(child, (depth || 0) + 1);
        if (hit) return hit;
    }
    return null;
}

function afEnrichUser(ctx) {
    if (!ctx || !ctx.id) return ctx;
    const live = (typeof vrcFriendsData !== 'undefined') && vrcFriendsData.find(x => x.id === ctx.id);
    const base = live || afObservedInstanceUsers()?.get(ctx.id) || afWatchState.lastInstanceUsers?.get(ctx.id);
    if (!base) return ctx;
    return Object.assign({}, base, {
        _worldName:   ctx._worldName   || base._worldName,
        _friendImage: ctx._friendImage || base._friendImage,
    });
}

function afUserIconValue(u) {
    if (!u) return null;
    const url = String(u.image || u._friendImage || '');
    const id  = String(u.id || '');
    if (!url && !id) return null;
    return { __afIcon: true, id, url };
}

function afTriggerUserIcon() {
    return afUserIconValue(afEnrichUser(afContext.triggeringUser || null));
}

function afFriendFromField(f) {
    const id = String((f && f.FRIEND_ID) || '').trim();
    const ctx = afContext.triggeringUser || null;
    if (id) return afLookupUser(id) || ctx;
    return afEnrichUser(ctx);
}

function afValueText(val) {
    if (val === null || val === undefined) return '';
    if (typeof val !== 'object') return String(val);
    if (val.__afIcon) return '';
    return String(val.displayName || val.name || val.id || '');
}

function afBundleText(child) {
    if (!child) return '';
    return afValueText(afEvalValue(child));
}

function afEvalValue(block) {
    if (!block) return null;
    const f = block.fields || {};
    switch (block.type) {
        case 'af_bundle_list': {
            const lines = [];
            for (let i = 0; i < 20; i++) {
                const text = afBundleText(afInput(block, 'ITEM' + i));
                if (text !== '') lines.push(text);
            }
            return lines.join('\n');
        }
        case 'af_text': return String(f.TEXT || '');
        case 'af_friend_icon': {
            return afUserIconValue(afFriendFromField(f));
        }
        case 'af_friend_status': {
            const u = afFriendFromField(f);
            if (!u) return '';
            return vrcStatusLabel(u.status || '');
        }
        case 'af_friend_status_text': {
            const u = afFriendFromField(f);
            return u ? String(u.statusDescription || '') : '';
        }
        case 'af_friend_world': {
            const u = afFriendFromField(f);
            if (!u) return '';
            const loc = String(u.location || '');
            if (!loc || loc === 'offline' || loc === 'private' || loc === 'traveling') return '';
            const worldId = loc.split(':')[0];
            return afWorldNameFor(worldId, u._worldName);
        }
        case 'af_friend_presence': {
            const u = afFriendFromField(f);
            if (!u) return aft('friend_info.presence_offline', 'Offline');
            const p = String(u.presence || '');
            if (p === 'game') return aft('friend_info.presence_game', 'In Game');
            if (p === 'web')  return aft('friend_info.presence_web', 'Active on Website');
            if (p === 'offline') return aft('friend_info.presence_offline', 'Offline');
            const loc = String(u.location || '');
            if (loc && loc !== 'offline') return aft('friend_info.presence_game', 'In Game');
            if ((u.status || 'offline') !== 'offline') return aft('friend_info.presence_web', 'Active on Website');
            return aft('friend_info.presence_offline', 'Offline');
        }
        case 'af_join_text': {
            const a = afBundleText(afInput(block, 'A'));
            const b = afBundleText(afInput(block, 'B'));
            if (!a) return b;
            if (!b) return a;
            return /\s$/.test(a) ? a + b : a + ' ' + b;
        }
        case 'af_bool':   return f.BOOL === 'TRUE';
        case 'af_number': return Number(f.VALUE);
        case 'af_get_condition': {
            const name = f.NAME;
            if (!name) return false;
            return !!afConditions[name];
        }
        case 'af_and':    return !!afEvalValue(afInput(block, 'A')) && !!afEvalValue(afInput(block, 'B'));
        case 'af_or':     return !!afEvalValue(afInput(block, 'A')) || !!afEvalValue(afInput(block, 'B'));
        case 'af_compare': {
            const a = afEvalValue(afInput(block, 'A'));
            const b = afEvalValue(afInput(block, 'B'));
            switch (f.OP) {
                case 'EQ': return afCmpEq(a, b);
                case 'GT': return Number(a) > Number(b);
                case 'LT': return Number(a) < Number(b);
            }
            return false;
        }
        case 'af_is_date': {
            const now = new Date();
            return now.getDate() === Number(f.DD) && (now.getMonth() + 1) === Number(f.MM) && now.getFullYear() === Number(f.YYYY);
        }
        case 'af_is_time': {
            const now = new Date();
            let hh = Number(f.HH);
            if (f.AMPM === 'PM' && hh < 12) hh += 12;
            if (f.AMPM === 'AM' && hh === 12) hh = 0;
            return now.getHours() === hh && now.getMinutes() === Number(f.MM);
        }
        case 'af_between_time': {
            const now = new Date();
            const toMins = (h, m, ampm) => {
                let hh = Number(h);
                if (ampm === 'PM' && hh < 12) hh += 12;
                if (ampm === 'AM' && hh === 12) hh = 0;
                return hh * 60 + Number(m);
            };
            const start = toMins(f.HH1, f.MM1, f.AMPM1);
            const end   = toMins(f.HH2, f.MM2, f.AMPM2);
            const cur   = now.getHours() * 60 + now.getMinutes();
            return start <= end ? (cur >= start && cur <= end) : (cur >= start || cur <= end);
        }
        case 'af_on_days': {
            /* getDay(): 0=Sun, 1=Mon, ..., 6=Sat */
            const slots = ['DAY_SU', 'DAY_MO', 'DAY_TU', 'DAY_WE', 'DAY_TH', 'DAY_FR', 'DAY_SA'];
            const checked = slots.filter(k => f[k] === 'TRUE' || f[k] === true);
            if (!checked.length) return true; /* empty = everyday */
            return checked.includes(slots[new Date().getDay()]);
        }
        case 'af_is_game_running':
            return !!afVrcGameRunning;
        case 'af_is_friend': {
            const u = afEvalUser(afInput(block, 'USER'));
            if (!u || !u.id) return false;
            if (typeof vrcFriendsData === 'undefined') return false;
            return vrcFriendsData.some(fr => fr.id === u.id);
        }
        case 'af_is_favorite_friend': {
            const favUser = afEvalUser(afInput(block, 'USER'));
            if (!favUser || !favUser.id) return false;
            if (typeof favFriendsData === 'undefined' || !Array.isArray(favFriendsData)) return false;
            const wantGroup = String(f.FAV_GROUP || '');
            if (!wantGroup) return false;
            return favFriendsData.some(fav => fav.favoriteId === favUser.id && fav.groupName === wantGroup);
        }
        case 'af_invite_from_friend':
            return afContext.triggerKind === 'invite'
                && !!afContext.triggeringUser
                && afContext.triggeringUser.id === f.FRIEND_ID;
        case 'af_invite_request_from_friend':
            return afContext.triggerKind === 'requestInvite'
                && !!afContext.triggeringUser
                && afContext.triggeringUser.id === f.FRIEND_ID;
        case 'af_has_status': {
            const u = afEvalUser(afInput(block, 'USER'));
            if (!u) return false;
            return (u.status || '') === f.STATUS;
        }
        case 'af_own_status': {
            const me = typeof currentVrcUser !== 'undefined' ? currentVrcUser : null;
            return !!me && (me.status || '') === f.STATUS;
        }
        case 'af_has_status_text': {
            const u = afEvalUser(afInput(block, 'USER'));
            return !!u && (u.statusDescription || '') === (f.TEXT || '');
        }
        case 'af_own_status_text': {
            const me = typeof currentVrcUser !== 'undefined' ? currentVrcUser : null;
            return !!me && (me.statusDescription || '') === (f.TEXT || '');
        }
        case 'af_has_bio_text': {
            const u = afEvalUser(afInput(block, 'USER'));
            return !!u && (u.bio || '') === (f.TEXT || '');
        }
        case 'af_own_bio_text': {
            const me = typeof currentVrcUser !== 'undefined' ? currentVrcUser : null;
            return !!me && (me.bio || '') === (f.TEXT || '');
        }
        case 'af_get_current_world': {
            const u = afEvalUser(afInput(block, 'USER'));
            if (!u) return null;
            let loc = u.location;
            if (!loc && typeof currentVrcUser !== 'undefined' && currentVrcUser && u.id === currentVrcUser.id) {
                loc = (typeof currentInstanceData !== 'undefined' && currentInstanceData && !currentInstanceData.empty)
                    ? currentInstanceData.location : null;
            }
            if (!loc) return null;
            return { id: String(loc).split(':')[0], kind: 'world' };
        }
        case 'af_get_my_avatar': {
            return typeof currentAvatarId !== 'undefined' ? (currentAvatarId || '') : '';
        }
        case 'af_avatar_obj': {
            return (f.AVATAR_ID || '').trim();
        }
        case 'af_get_user_count': {
            if (typeof currentInstanceData === 'undefined' || !currentInstanceData || currentInstanceData.empty) return 0;
            if (Array.isArray(currentInstanceData.users) && currentInstanceData.users.length > 0)
                return currentInstanceData.users.length;
            return Number(currentInstanceData.nUsers) || 0;
        }
        case 'af_get_instance_type': {
            if (typeof currentInstanceData === 'undefined' || !currentInstanceData || currentInstanceData.empty) return '';
            const raw = String(currentInstanceData.instanceType || '');
            if (raw === 'hidden')        return 'friends+';
            if (raw === 'group-members') return 'group';
            return raw;
        }
        case 'af_instance_type_obj': {
            return f.INSTANCE_TYPE || '';
        }
        case 'af_is_my_avatar': {
            const want = (f.AVATAR_ID || '').trim();
            const cur  = typeof currentAvatarId !== 'undefined' ? (currentAvatarId || '') : '';
            return !!want && want === cur;
        }
        case 'af_in_same_instance': {
            const u = afEvalUser(afInput(block, 'USER'));
            if (!u || !u.id) return false;
            if (typeof currentInstanceData !== 'undefined'
                && currentInstanceData
                && !currentInstanceData.empty
                && !currentInstanceData.error
                && Array.isArray(currentInstanceData.users)
                && currentInstanceData.users.some(x => x && x.id === u.id)) {
                return true;
            }
            if (!u.location || !String(u.location).startsWith('wrld_')) return false;
            const myLocRaw =
                (typeof currentInstanceData !== 'undefined' && currentInstanceData?.location) ||
                (typeof currentVrcUser       !== 'undefined' && currentVrcUser?.location) ||
                '';
            if (!myLocRaw || !myLocRaw.startsWith('wrld_')) return false;
            const myBase    = String(myLocRaw).split('~')[0];
            const theirBase = String(u.location).split('~')[0];
            return myBase === theirBase;
        }
        case 'af_get_world_name': {
            const ci = afCurInst();
            return ci ? String(ci.worldName || '') : '';
        }
        case 'af_get_avatar_name': {
            const id = typeof currentAvatarId !== 'undefined' ? (currentAvatarId || '') : '';
            if (!id) return '';
            const cached = (typeof avatarInfoCache !== 'undefined' && avatarInfoCache[id]) || null;
            if (cached && cached.name) return String(cached.name);
            const own = (typeof avatarsData !== 'undefined' && avatarsData.find(a => a && a.id === id)) || null;
            if (own && own.name) return String(own.name);
            const fav = (typeof favAvatarsData !== 'undefined' && favAvatarsData.find(a => a && a.id === id)) || null;
            return (fav && fav.name) ? String(fav.name) : '';
        }
        case 'af_get_instance_name': {
            const ci = afCurInst();
            return ci ? String(ci.displayName || '') : '';
        }
        case 'af_get_instance_id': {
            const ci = afCurInst();
            if (!ci || !ci.location) return '';
            const after = String(ci.location).split(':')[1] || '';
            return after.split('~')[0] || '';
        }
        case 'af_get_player_count': {
            const ci = afCurInst();
            if (!ci) return '';
            const n   = Array.isArray(ci.users) && ci.users.length > 0 ? ci.users.length : (Number(ci.nUsers) || 0);
            const cap = Number(ci.capacity) || 0;
            return String(n).padStart(2, '0') + '/' + cap;
        }
        case 'af_get_player_list': {
            const ci = afCurInst();
            if (!ci || !Array.isArray(ci.users)) return '';
            return ci.users.map(u => (u && u.displayName) || '').filter(Boolean).join(', ');
        }
        case 'af_get_ingame_friends': {
            if (typeof vrcFriendsData === 'undefined' || !Array.isArray(vrcFriendsData)) return '';
            return vrcFriendsData
                .filter(fr => fr && (fr.status || 'offline') !== 'offline' && !!fr.location && fr.location !== 'offline')
                .map(fr => fr.displayName || '')
                .filter(Boolean)
                .join(', ');
        }
        case 'af_get_time': {
            const now = new Date();
            return typeof fmtTime === 'function' ? fmtTime(now) : now.toLocaleTimeString();
        }
        case 'af_get_heart_rate_int':
            return typeof hypeRateBpm !== 'undefined' ? Math.round(Number(hypeRateBpm) || 0) : 0;
        case 'af_get_joined_player_name': {
            const tu = afContext.triggeringUser;
            return tu ? String(tu.displayName || afInstanceUserName(tu.id) || tu.id || '') : '';
        }
        case 'af_get_left_player_name': {
            if (afContext.triggerAction !== 'leave') return '';
            const tu = afContext.triggeringUser;
            return tu ? String(tu.displayName || afInstanceUserName(tu.id) || tu.id || '') : '';
        }
        case 'af_get_joined_player_image': return afTriggerUserIcon();
        case 'af_get_left_player_image':
            return afContext.triggerAction === 'leave' ? afTriggerUserIcon() : null;
        case 'af_get_instance_type_label': {
            const ci = afCurInst();
            if (!ci) return '';
            const raw = String(ci.instanceType || '');
            if (!raw) return '';
            return afInstanceTypeLabel(raw === 'hidden' ? 'friends+' : raw);
        }
        case 'af_friend_obj':
        case 'af_user_obj':
        case 'af_own_user':
        case 'af_triggering_user':
        case 'af_world_obj':
            return afEvalUser(block);
    }
    return null;
}

function afCurInst() {
    if (typeof currentInstanceData === 'undefined' || !currentInstanceData) return null;
    if (currentInstanceData.empty || currentInstanceData.error) return null;
    return currentInstanceData;
}

function afCmpEq(a, b) {
    if (a && typeof a === 'object' && b && typeof b === 'object') {
        if (a.id && b.id) return a.id === b.id;
    }
    if (a && typeof a === 'object' && a.id) return String(a.id) === String(b);
    if (b && typeof b === 'object' && b.id) return String(b.id) === String(a);
    return String(a) === String(b);
}

function afEvalUser(block) {
    if (!block) return null;
    const f = block.fields || {};
    switch (block.type) {
        case 'af_friend_obj':
            return afLookupUser(f.FRIEND_ID);
        case 'af_user_obj':
            return afLookupUser(f.USER_ID);
        case 'af_own_user': {
            return (typeof currentVrcUser !== 'undefined' && currentVrcUser) || null;
        }
        case 'af_triggering_user': {
            return afContext.triggeringUser;
        }
        case 'af_world_obj': {
            return { id: f.WORLD_ID, kind: 'world' };
        }
        case 'af_get_current_world': {
            return afEvalValue(block);
        }
    }
    return null;
}

window.afOnTabOpen = async function afOnTabOpen() {
    if (afTabInitialized) return;
    afTabInitialized = true;
    try { await afInitWorkspace(); }
    catch (e) {
        const hint = document.getElementById('afLoadingHint');
        if (hint) hint.innerHTML = '<span class="msi" style="font-size:32px;color:var(--err);">error</span><div style="font-size:calc(13px + var(--fs-off, 0px));color:var(--err);margin-top:8px;">Failed to load Blockly: ' + afEsc(e.message || e) + '</div>';
        return;
    }
    afRenderConditionsPanel();
    if (typeof sendToCS === 'function') sendToCS({ action: 'afLoadFlows' });
};

window.afNewFlow            = afNewFlow;
window.afRenameFlow         = afRenameFlow;
window.afDeleteFlow         = afDeleteFlow;
window.afSelectFlow         = afSelectFlow;
window.afToggleEnabled      = afToggleEnabled;
window.afSaveCurrentFlow    = afSaveCurrentFlow;
window.afRunNow             = afRunNow;
window.afClearLog           = afClearLog;
window.afToggleLogPanel     = afToggleLogPanel;

window.afZoom = function (dir) {
    if (!afWorkspace) return;
    afWorkspace.zoomCenter(dir);
};
window.afZoomReset = function () {
    if (!afWorkspace) return;
    afWorkspace.setScale(0.95);
    afWorkspace.scrollCenter();
};
window.afDeleteSelected = function () {
    if (!afWorkspace) return;
    const sel = window.Blockly.getSelected && window.Blockly.getSelected();
    if (sel && typeof sel.dispose === 'function') sel.dispose(true);
};

function afShowFlowNotificationCard(flowName, text, icon) {
    const area = document.getElementById('notifCardArea');
    if (!area) return;
    const card = document.createElement('div');
    card.className = 'nc-card';
    const iconHtml = (icon && icon.url)
        ? '<img class="nc-icon af-nc-avatar" src="' + afEsc(imgThumb(icon.url, 64)) + '" alt="">'
        : '<span class="msi nc-icon" style="color:var(--accent);">auto_awesome</span>';
    card.innerHTML =
        '<div class="nc-inner">' +
            iconHtml +
            '<div class="nc-body">' +
                '<div class="nc-title"><strong>' + afEsc(flowName) + '</strong></div>' +
                (text ? '<div class="nc-sub">' + afEsc(text) + '</div>' : '') +
            '</div>' +
            '<button class="nc-close-btn" title="Close"><span class="msi" style="font-size:15px;">close</span></button>' +
        '</div>' +
        '<div class="nc-timer"><div class="nc-timer-bar" style="background:var(--accent);"></div></div>';
    area.appendChild(card);
    const close = () => { if (card.parentNode) { card.classList.remove('nc-visible'); setTimeout(() => card.remove(), 350); } };
    card.querySelector('.nc-close-btn').addEventListener('click', close);
    requestAnimationFrame(() => {
        card.classList.add('nc-visible');
        const bar = card.querySelector('.nc-timer-bar');
        if (bar) {
            bar.style.transition = 'transform 8s linear';
            requestAnimationFrame(() => { bar.style.transform = 'scaleX(0)'; });
        }
    });
    setTimeout(close, 8200);
}

window.afBuildBlockContextMenu = function (target) {
    if (!afWorkspace || !window.Blockly || !target) return null;
    const blockEl = target.closest('.blocklyDraggable');
    if (!blockEl) return null;
    const id = blockEl.getAttribute('data-id');
    if (!id) return null;
    const block = afWorkspace.getBlockById(id);
    if (!block) return null;

    const items = [];
    if (typeof block.isDeletable === 'function' && block.isDeletable()) {
        items.push({ icon: 'content_copy', label: aft('ctx.duplicate', 'Duplicate'), action: () => {
            try {
                const json = window.Blockly.serialization.blocks.save(block);
                const dup = window.Blockly.serialization.blocks.append(json, afWorkspace);
                const xy = block.getRelativeToSurfaceXY();
                if (dup && dup.moveBy) dup.moveBy(20, 20);
                if (dup && dup.select) dup.select();
            } catch (e) { afLog('err', aftf('log.duplicate_failed', { error: e.message || e }, 'Duplicate failed: ' + (e.message || e))); }
        }});
    }
    if (typeof block.setCommentText === 'function') {
        const hasComment = !!block.getCommentText?.();
        items.push({
            icon: hasComment ? 'speaker_notes_off' : 'add_comment',
            label: hasComment ? aft('ctx.remove_comment', 'Remove Comment') : aft('ctx.add_comment', 'Add Comment'),
            action: () => block.setCommentText(hasComment ? null : ''),
        });
    }
    if (typeof block.setCollapsed === 'function') {
        const collapsed = block.isCollapsed();
        items.push({
            icon: collapsed ? 'unfold_more' : 'unfold_less',
            label: collapsed ? aft('ctx.expand', 'Expand Block') : aft('ctx.collapse', 'Collapse Block'),
            action: () => block.setCollapsed(!collapsed),
        });
    }
    if (typeof block.setEnabled === 'function') {
        const enabled = block.isEnabled();
        items.push({
            icon: enabled ? 'block' : 'check_circle',
            label: enabled ? aft('ctx.disable', 'Disable Block') : aft('ctx.enable', 'Enable Block'),
            action: () => block.setEnabled(!enabled),
        });
    }
    if (typeof block.isDeletable === 'function' && block.isDeletable()) {
        const count = block.getDescendants ? block.getDescendants(true).length : 1;
        items.push('sep');
        items.push({
            icon: 'delete',
            label: count > 1 ? aftf('ctx.delete_n', { count }, 'Delete ' + count + ' Blocks') : aft('ctx.delete_one', 'Delete Block'),
            action: () => block.dispose(true),
        });
    }
    return items.length ? items : null;
};

window.__afHandleMessage = function (action, payload) {
    switch (action) {
        case 'afFlows':
            afCancelWaits();
            afResetCooldowns();
            afFlows = Array.isArray(payload?.flows) ? payload.flows : [];
            for (const f of afFlows) {
                if (!f.id) f.id = afNewId();
                if (typeof f.enabled !== 'boolean') f.enabled = false;
            }
            afConditions = (payload && payload.conditions && typeof payload.conditions === 'object') ? payload.conditions : {};
            afRenderFlowSelect();
            if (afFlows.length && !afCurrentFlowId) afCurrentFlowId = afFlows[0].id;
            if (afWorkspace) afLoadFlowIntoWorkspace(afCurrentFlowId);
            const en = document.getElementById('afFlowEnabled');
            const cur = afFlows.find(f => f.id === afCurrentFlowId);
            if (en) en.checked = !!(cur && cur.enabled);
            afUpdateRunIndicator();
            afRenderConditionsPanel();
            break;
        case 'afSaveResult':
            if (payload && payload.ok === false) {
                afLog('err', aftf('toast.save_failed', { error: payload.error || 'unknown' }, 'Flow save failed: ' + (payload.error || 'unknown')));
                if (typeof showToast === 'function') showToast(false, aftf('toast.save_failed', { error: payload.error || 'unknown' }, 'Flow save failed: ' + (payload.error || 'unknown')));
            }
            break;
        case 'afGameRunning':
            afVrcGameRunning = !!(payload && payload.running);
            break;
    }
};

function afBoot() {
    if (typeof sendToCS === 'function') sendToCS({ action: 'afLoadFlows' });
    afStartTicker();
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', afBoot);
} else {
    afBoot();
}

})();
