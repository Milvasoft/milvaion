package com.milvasoft.milvaion.worker;

import java.io.PrintWriter;
import java.io.StringWriter;

final class Errors {
    private Errors() {
    }

    /** Renders an exception the way the .NET SDK does, so the dashboard shows both alike. */
    static String format(Throwable error) {
        StringBuilder text = new StringBuilder()
                .append("Type: ").append(error.getClass().getName()).append('\n')
                .append("Message: ").append(error.getMessage()).append('\n')
                .append("StackTrace: ").append(stackTrace(error)).append('\n');

        if (error.getCause() != null && error.getCause() != error) {
            text.append("\nInner Exception:\n").append(format(error.getCause()));
        }

        return text.toString();
    }

    private static String stackTrace(Throwable error) {
        StringWriter writer = new StringWriter();
        Throwable copy = new Throwable();
        copy.setStackTrace(error.getStackTrace());
        copy.printStackTrace(new PrintWriter(writer));

        // Drop the "java.lang.Throwable" header line of the copy.
        String trace = writer.toString();
        int newline = trace.indexOf('\n');

        return newline >= 0 ? trace.substring(newline + 1) : trace;
    }
}
