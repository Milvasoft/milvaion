package com.milvasoft.milvaion.worker;

import java.util.ArrayList;
import java.util.List;

/** Abort state of one execution. */
final class Abort {
    private JobAbortedException.Reason reason;
    private String message = "";
    private final List<Runnable> listeners = new ArrayList<>();

    synchronized JobAbortedException.Reason reason() {
        return reason;
    }

    synchronized String message() {
        return message;
    }

    /** Returns false if the job was already aborted. */
    boolean trigger(JobAbortedException.Reason reason, String message) {
        List<Runnable> toRun;

        synchronized (this) {
            if (this.reason != null) return false;

            this.reason = reason;
            this.message = message;
            toRun = new ArrayList<>(listeners);
        }

        toRun.forEach(Runnable::run);

        return true;
    }

    void onAbort(Runnable listener) {
        synchronized (this) {
            if (reason == null) {
                listeners.add(listener);
                return;
            }
        }

        listener.run();
    }
}
