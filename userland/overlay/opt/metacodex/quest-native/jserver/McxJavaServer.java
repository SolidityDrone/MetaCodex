// McxJavaServer: the warm JVM of mcx-buildd for 2D panel apps. It runs javac (javax.tools) and D8 (its API, from the
// R8 jar) inside this one process, so a build pays no JVM start and runs on JIT-compiled tool code.
//
// mcx-buildd starts it (java -cp <these classes>:<r8.jar> McxJavaServer), on cores 0-2 at the daemon's nice level, and
// is its only client, over stdin / stdout:
//   request: "RUN <tool> <n>\n" then n lines, one argument each   (tool: javac | d8 | ping)
//   answer:  "OUT <bytes>\n" <the tool's output, UTF-8> "EXIT <code> <ms>\n"
// It answers "READY <java version>\n" once at start. Stray prints of the tools go into the answer, never onto the
// protocol stream. After each answer it collects garbage so an idle server gives memory back. It ends when stdin
// closes (mcx-buildd stops it after a few idle minutes). Shipped by MetaCodex (overlay); edits are overwritten.

import com.android.tools.r8.CompilationFailedException;
import com.android.tools.r8.D8;
import com.android.tools.r8.D8Command;
import com.android.tools.r8.DiagnosticsHandler;
import com.android.tools.r8.origin.Origin;

import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.FileDescriptor;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import javax.tools.JavaCompiler;
import javax.tools.ToolProvider;

public final class McxJavaServer {
    public static void main(String[] argv) throws IOException {
        BufferedReader in = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        OutputStream wire = new FileOutputStream(FileDescriptor.out);
        PrintStream realOut = System.out, realErr = System.err;
        JavaCompiler javac = ToolProvider.getSystemJavaCompiler();
        // D8's worker threads: two, like mcx-build's compile jobs (cores 0-2; the headset keeps 72 fps)
        ExecutorService pool = Executors.newFixedThreadPool(2, r -> {
            Thread t = new Thread(r, "d8");
            t.setDaemon(true);
            return t;
        });
        wire.write(("READY " + System.getProperty("java.version") + "\n").getBytes(StandardCharsets.UTF_8));
        wire.flush();
        String line;
        while ((line = in.readLine()) != null) {
            String[] h = line.trim().split(" ");
            if (h.length != 3 || !h[0].equals("RUN")) {
                answer(wire, "mcx-javaserver: bad request: " + line + "\n", 2, 0);
                continue;
            }
            int n;
            try {
                n = Integer.parseInt(h[2]);
            } catch (NumberFormatException e) {
                answer(wire, "mcx-javaserver: bad count " + h[2] + "\n", 2, 0);
                continue;
            }
            String[] args = new String[n];
            for (int i = 0; i < n; i++) {
                String a = in.readLine();
                if (a == null) return;
                args[i] = a;
            }
            ByteArrayOutputStream buf = new ByteArrayOutputStream();
            PrintStream ps = new PrintStream(buf, true, StandardCharsets.UTF_8);
            long t0 = System.nanoTime();
            int code;
            System.setOut(ps);
            System.setErr(ps);
            try {
                switch (h[1]) {
                    case "javac":
                        code = javac == null ? 3 : javac.run(null, ps, ps, args);
                        if (javac == null) ps.println("mcx-javaserver: this Java has no compiler (a JRE): install a JDK");
                        break;
                    case "d8":
                        code = d8(args, ps, pool);
                        break;
                    case "ping":
                        code = 0;
                        break;
                    default:
                        ps.println("mcx-javaserver: unknown tool " + h[1]);
                        code = 2;
                }
            } catch (Throwable t) {
                t.printStackTrace(ps);
                code = 1;
            } finally {
                System.setOut(realOut);
                System.setErr(realErr);
            }
            ps.flush();
            answer(wire, buf.toString(StandardCharsets.UTF_8), code, (System.nanoTime() - t0) / 1_000_000L);
            System.gc(); // after the answer: the build does not wait for it, and an idle server shrinks
        }
    }

    private static int d8(String[] args, PrintStream ps, ExecutorService pool) throws CompilationFailedException {
        DiagnosticsHandler handler = new DiagnosticsHandler() {
            @Override
            public void error(com.android.tools.r8.Diagnostic d) {
                DiagnosticsHandler.printDiagnosticToStream(d, "Error", ps);
            }

            @Override
            public void warning(com.android.tools.r8.Diagnostic d) {
                DiagnosticsHandler.printDiagnosticToStream(d, "Warning", ps);
            }

            @Override
            public void info(com.android.tools.r8.Diagnostic d) {
                DiagnosticsHandler.printDiagnosticToStream(d, "Info", ps);
            }
        };
        D8Command cmd;
        try {
            cmd = D8Command.parse(args, Origin.root(), handler).build();
        } catch (CompilationFailedException e) {
            return 1; // the handler printed why
        }
        try {
            D8.run(cmd, pool);
            return 0;
        } catch (CompilationFailedException e) {
            return 1;
        }
    }

    private static void answer(OutputStream wire, String text, int code, long ms) throws IOException {
        byte[] b = text.getBytes(StandardCharsets.UTF_8);
        wire.write(("OUT " + b.length + "\n").getBytes(StandardCharsets.UTF_8));
        wire.write(b);
        wire.write(("EXIT " + code + " " + ms + "\n").getBytes(StandardCharsets.UTF_8));
        wire.flush();
    }
}
