const NAV_ITEMS_DEF = {
    'dashboard':        { icon: 'dashboard',       tab: 0,  i18n: 'nav.dashboard',         label: 'Dashboard'        },
    'worlds':           { icon: 'travel_explore',  tab: 1,  i18n: 'nav.worlds',             label: 'Worlds'           },
    'groups':           { icon: 'groups',          tab: 2,  i18n: 'nav.groups',             label: 'Groups'           },
    'people':           { icon: 'person_search',   tab: 3,  i18n: 'nav.people',             label: 'People'           },
    'calendar':         { icon: 'calendar_month',  tab: 17, i18n: 'nav.calendar',           label: 'Calendar'         },
    'avatars':          { icon: 'checkroom',       tab: 4,  i18n: 'nav.avatars',            label: 'Avatars'          },
    'inventory':        { icon: 'inventory_2',     tab: 13, i18n: 'nav.inventory',          label: 'Inventory'        },
    'timeline':         { icon: 'timeline',        tab: 12, i18n: 'nav.timeline',           label: 'Timeline'         },
    'media-library':    { icon: 'photo_library',   tab: 7,  i18n: 'nav.media_library',      label: 'Media Library'    },
    'settings':         { icon: 'settings',        tab: 9,  i18n: 'nav.settings',           label: 'Settings'         },
    'chatbox':          { icon: 'chat',            tab: 5,  i18n: 'nav.custom_chatbox',     label: 'Custom Chatbox',   windowsOnly: true },
    'media-relay':      { icon: 'cell_tower',      tab: 6,  i18n: 'nav.media_relay',        label: 'Media Relay',      windowsOnly: true },
    'space-flight':     { icon: 'rocket_launch',   tab: 10, i18n: 'nav.space_flight',       label: 'Space Flight',     windowsOnly: true },
    'frame-shot':       { icon: 'photo_camera',    tab: 26, i18n: 'nav.frame_shot',         label: 'FrameShot',        windowsOnly: true },
    'osc-tool':         { icon: 'tune',            tab: 11, i18n: 'nav.osc_tool',           label: 'OSC Tool',         windowsOnly: true },
    'youtube-fix':      { icon: 'smart_display',   tab: 14, i18n: 'nav.youtube_fix',        label: 'YouTube Fix',      windowsOnly: true },
    'activity-log':     { icon: 'article',         tab: 8,  i18n: 'nav.activity_log',       label: 'Activity Log'     },
    'mutual-network':   { icon: 'hub',             tab: 15, i18n: 'nav.mutual_network',     label: 'Mutual Network'   },
    'time-spent':       { icon: 'schedule',        tab: 16, i18n: 'nav.time_spent',         label: 'Time Spent'       },
    'voice-fight':      { icon: 'mic',             tab: 18, i18n: 'nav.voice_fight',        label: 'Voice Fight',      windowsOnly: true },
    'discord-presence': { icon: 'sensors',         tab: 19, i18n: 'nav.discord_presence',   label: 'Discord Presence', windowsOnly: true },
    'vr-overlay':       { icon: 'watch',           tab: 20, i18n: 'nav.vr_overlay',         label: 'VR Overlay',       windowsOnly: true },
    'permini':          { icon: 'lock',            tab: 21, i18n: 'nav.permini',            label: 'Permini'          },
    'kikitan-xd':       { icon: 'translate',       tab: 22, i18n: 'nav.kikitan_xd',         label: 'Kikitan XD',       windowsOnly: true },
    'event-snipe':      { icon: 'gps_fixed',       tab: 23, i18n: 'nav.event_snipe',        label: 'Event Snipe'      },
    'avatar-scaling':   { icon: 'height',          tab: 24, i18n: 'nav.avatar_scaling',     label: 'Avatar Scaling',   windowsOnly: true },
    'action-flow':      { icon: 'auto_awesome',    tab: 25, i18n: 'nav.action_flow',        label: 'Action Flow'      },
};

const NAV_ICON_OPTIONS = [
    // Nav defaults
    'dashboard','travel_explore','groups','person_search','calendar_month','checkroom',
    'inventory_2','timeline','photo_library','settings','chat','cell_tower','rocket_launch',
    'photo_camera',
    'tune','smart_display','article','hub','schedule','mic','sensors','watch','lock',
    'translate','gps_fixed','height','build','folder','star','favorite','bookmark',
    'home','explore','map','music_note','videocam','image','code','analytics','bar_chart',
    'notifications','sports_esports','emoji_events','diamond','auto_awesome','bolt',
    'local_fire_department','cloud','terminal','bug_report','water_drop','edit',
    // People & Social
    'person','manage_accounts','face','badge','group_add','waving_hand','contacts',
    'handshake','supervisor_account','record_voice_over','diversity_3','diversity_1',
    // VR / Gaming
    'view_in_ar','vrpano','3d_rotation','videogame_asset','joystick','stadia_controller',
    'headset','headset_mic','precision_manufacturing','gamepad',
    // Media & Creative
    'palette','brush','photo_camera','movie','live_tv','radio','podcasts',
    'collections','camera_roll','draw','color_lens','theaters',
    // Communication
    'forum','message','mail','send','call','video_call','comment','chat_bubble',
    'mark_chat_unread','question_answer','sms',
    // Status / Badges
    'flag','label','new_releases','workspace_premium','military_tech',
    'verified','shield','key','vpn_key','security','privacy_tip','info','warning',
    // Navigation / Location
    'location_on','place','directions','navigation','pin_drop','my_location','public','language',
    // Time
    'event','today','timer','alarm','access_time','date_range','history',
    // Files / Data
    'description','folder_open','download','upload','cloud_upload','sync','storage','dns','backup',
    // UI / Misc
    'grid_view','list','category','extension','widgets','apps','dashboard_customize',
    'search','filter_list','sort','refresh','add_circle','link','share','open_in_new',
    'dark_mode','brightness_high','wifi','bluetooth','power','emoji_objects','psychology',
    'local_activity','celebration','cake','pets','spa','nature','forest','whatshot',
];

const NAV_DEFAULT_LAYOUT = [
    { type: 'item', key: 'dashboard' },
    { type: 'item', key: 'worlds' },
    { type: 'item', key: 'groups' },
    { type: 'item', key: 'people' },
    { type: 'item', key: 'calendar' },
    { type: 'item', key: 'avatars' },
    { type: 'item', key: 'inventory' },
    { type: 'item', key: 'timeline' },
    { type: 'item', key: 'media-library' },
    {
        type: 'folder', id: 'folder-tools', name: 'Tools', icon: 'build',
        items: [
            'chatbox','media-relay','space-flight','frame-shot','osc-tool','youtube-fix',
            'activity-log','mutual-network','time-spent','voice-fight',
            'discord-presence','vr-overlay','permini','kikitan-xd','event-snipe','avatar-scaling','action-flow',
        ],
    },
    { type: 'item', key: 'settings' },
];

const _NAV_STORAGE_KEY = 'vrcnext_nav_layout_v1';

function navLoadLayout() {
    try {
        const raw = localStorage.getItem(_NAV_STORAGE_KEY);
        if (!raw) return { layout: _navClone(NAV_DEFAULT_LAYOUT), hidden: [] };
        const parsed = JSON.parse(raw);
        const hidden = Array.isArray(parsed.hidden)
                ? parsed.hidden.filter(k => k in NAV_ITEMS_DEF)
                : [];
        return {
            layout: navSanitizeLayout(parsed.layout || [], hidden),
            hidden,
        };
    } catch {
        return { layout: _navClone(NAV_DEFAULT_LAYOUT), hidden: [] };
    }
}

function navSaveLayout(layout, hidden) {
    localStorage.setItem(_NAV_STORAGE_KEY, JSON.stringify({ layout, hidden }));
}

function navSanitizeLayout(layout, hidden = []) {
    const allKeys = Object.keys(NAV_ITEMS_DEF);
    const hiddenSet = new Set(hidden);
    const seen = new Set();
    const result = [];

    for (const entry of layout) {
        if (entry.type === 'item') {
            if (!allKeys.includes(entry.key) || seen.has(entry.key)) continue;
            seen.add(entry.key);
            result.push({ type: 'item', key: entry.key, icon: entry.icon || null });
        } else if (entry.type === 'folder') {
            const items = (entry.items || []).filter(k => {
                if (!allKeys.includes(k) || seen.has(k)) return false;
                seen.add(k);
                return true;
            });
            result.push({
                type: 'folder',
                id: entry.id || _navMakeFolderId(),
                name: entry.name || 'Folder',
                icon: entry.icon || 'folder',
                items,
            });
        }
    }

    for (const key of allKeys) {
        if (!seen.has(key) && !hiddenSet.has(key)) result.push({ type: 'item', key, icon: null });
    }
    return result;
}

function _navClone(o) { return JSON.parse(JSON.stringify(o)); }
function _navMakeFolderId() { return 'folder-' + Math.random().toString(36).slice(2, 9); }
