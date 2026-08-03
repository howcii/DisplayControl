package com.displaycontrol.androidagent;

/** 上报给服务器的屏幕状态快照。 */
final class StatusSnapshot {
    final String state;
    final boolean verified;
    final String details;
    final String source;

    StatusSnapshot(String state, boolean verified, String details, String source) {
        this.state = state;
        this.verified = verified;
        this.details = details;
        this.source = source;
    }
}
