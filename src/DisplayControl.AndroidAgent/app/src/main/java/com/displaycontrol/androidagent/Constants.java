package com.displaycontrol.androidagent;

/** 项目级常量集中管理，方便后续 V1.7/V1.8 迭代时查找协议名称。 */
final class Constants {
    static final String AGENT_VERSION = "Android Agent V1.6.9";
    static final int PROTOCOL_VERSION = 3;

    static final String ACTION_STATUS_CHANGED = "com.displaycontrol.androidagent.STATUS_CHANGED";
    static final String ACTION_CLOSE_BLACKOUT = "com.displaycontrol.androidagent.CLOSE_BLACKOUT";
    static final String ACTION_START_SERVICE = "com.displaycontrol.androidagent.START_SERVICE";
    static final String ACTION_STOP_SERVICE = "com.displaycontrol.androidagent.STOP_SERVICE";

    static final int DEFAULT_POLL_MS = 1000;
    static final int HTTP_TIMEOUT_MS = 6000;
    static final int MAX_JOURNAL_ITEMS = 50;
    static final long COMMAND_JOURNAL_TTL_MS = 24L * 60L * 60L * 1000L;

    private Constants() {
    }
}
