package com.displaycontrol.serialprobe;

import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbManager;
import android.os.Build;
import android.os.Bundle;
import android.view.Gravity;
import android.view.View;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Date;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import java.util.regex.Pattern;

/** Lenovo RS232 protocol probe. It only sends safe query commands by default. */
public class MainActivity extends Activity {
    private static final int BACKGROUND = Color.rgb(244, 247, 251);
    private static final int SURFACE = Color.WHITE;
    private static final int PRIMARY_TEXT = Color.rgb(15, 23, 42);
    private static final int SECONDARY_TEXT = Color.rgb(100, 116, 139);
    private static final int PRIMARY = Color.rgb(37, 99, 235);
    private static final int SUCCESS = Color.rgb(22, 101, 52);
    private static final int DANGER = Color.rgb(185, 28, 28);
    private static final Pattern SERIAL_NAME_PATTERN = Pattern.compile(
        "tty(S|USB|ACM|AMA|MT|SAC|MSM|HSL|HS|FIQ|XRUSB|GS)[0-9]+",
        Pattern.CASE_INSENSITIVE);

    private static final byte[] QUERY_PROTOCOL = hex("99 24 0E 00 2A AA");
    private static final byte[] QUERY_POWER = hex("99 24 01 00 25 AA");

    private final ArrayList<String> portPaths = new ArrayList<>();
    private ArrayAdapter<String> portAdapter;
    private Spinner portSpinner;
    private TextView stateText;
    private TextView resultText;
    private Button scanButton;
    private Button testSelectedButton;
    private Button testAllButton;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
            getWindow().setStatusBarColor(BACKGROUND);
            getWindow().setNavigationBarColor(BACKGROUND);
        }
        setContentView(buildContent());
        scanPortsAsync();
    }

    private View buildContent() {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(BACKGROUND);

        FrameLayout frame = new FrameLayout(this);
        scroll.addView(frame, new ScrollView.LayoutParams(
            ScrollView.LayoutParams.MATCH_PARENT,
            ScrollView.LayoutParams.WRAP_CONTENT));

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(18), dp(18), dp(18), dp(24));
        frame.addView(root, new FrameLayout.LayoutParams(
            contentWidth(),
            FrameLayout.LayoutParams.WRAP_CONTENT,
            Gravity.TOP | Gravity.CENTER_HORIZONTAL));

        LinearLayout header = card();
        TextView title = text("联想 RS232 串口检测", 25, PRIMARY_TEXT, true);
        TextView subtitle = text("检测 Android 系统能否直接访问大屏串口 · 只发送查询命令", 14, SECONDARY_TEXT, false);
        header.addView(title);
        header.addView(subtitle);
        root.addView(header);

        LinearLayout summary = card();
        summary.addView(sectionTitle("检测说明"));
        summary.addView(text(
            "本工具用于判断大屏 Android 系统里是否存在可读写串口。默认波特率 19200，按联想 V0.6 协议发送：协议版本查询 99 24 0E 00 2A AA，电源状态查询 99 24 01 00 25 AA。收到 CC ... DD 返回包，才说明 Android APK 直接串口控制有希望。",
            14,
            SECONDARY_TEXT,
            false));
        root.addView(summary);

        LinearLayout control = card();
        control.addView(sectionTitle("串口选择"));
        stateText = text("正在扫描…", 15, PRIMARY, true);
        stateText.setPadding(0, 0, 0, dp(10));
        control.addView(stateText);
        portSpinner = new Spinner(this);
        portAdapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, portPaths);
        portAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        portSpinner.setAdapter(portAdapter);
        control.addView(portSpinner, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            dp(48)));
        control.addView(buttonRow(
            blueButton("重新扫描", v -> scanPortsAsync()),
            whiteButton("测试选中串口", v -> testSelectedAsync())));
        control.addView(buttonRow(
            whiteButton("测试全部串口", v -> testAllAsync()),
            whiteButton("复制结果", v -> copyResult())));
        root.addView(control);

        LinearLayout result = card();
        result.addView(sectionTitle("检测结果"));
        resultText = text("", 13, Color.rgb(51, 65, 85), false);
        resultText.setTypeface(Typeface.MONOSPACE);
        resultText.setTextIsSelectable(true);
        resultText.setLineSpacing(3, 1.0f);
        resultText.setPadding(dp(12), dp(12), dp(12), dp(12));
        resultText.setBackground(round(Color.rgb(248, 250, 252), Color.rgb(226, 232, 240), 12, 1));
        result.addView(resultText);
        root.addView(result);

        TextView note = text(
            "说明：如果显示“权限不足”或“没有返回”，不是程序故障，而是系统没有把主板串口开放给普通 APK。此时需要厂商 SDK、系统应用权限、root/ADB 授权，或继续使用黑幕模式。",
            13,
            SECONDARY_TEXT,
            false);
        note.setPadding(dp(4), 0, dp(4), 0);
        root.addView(note);

        return scroll;
    }

    private void scanPortsAsync() {
        setBusy(true);
        stateText.setText("正在扫描 /dev 串口…");
        new Thread(() -> {
            StringBuilder report = new StringBuilder();
            report.append(headerLine("扫描开始"));
            ArrayList<PortInfo> found = scanPorts(report);
            report.append("\n").append(getUsbSummary());
            runOnUiThread(() -> {
                portPaths.clear();
                for (PortInfo info : found) {
                    portPaths.add(info.path);
                }
                portAdapter.notifyDataSetChanged();
                stateText.setText(found.isEmpty()
                    ? "未发现常见 /dev/tty* 串口"
                    : "发现 " + found.size() + " 个候选串口");
                stateText.setTextColor(found.isEmpty() ? DANGER : SUCCESS);
                resultText.setText(report.toString());
                setBusy(false);
            });
        }, "serial-probe-scan").start();
    }

    private ArrayList<PortInfo> scanPorts(StringBuilder report) {
        Set<String> candidates = new TreeSet<>();
        Collections.addAll(candidates,
            "/dev/ttyS0", "/dev/ttyS1", "/dev/ttyS2", "/dev/ttyS3",
            "/dev/ttyUSB0", "/dev/ttyUSB1",
            "/dev/ttyACM0", "/dev/ttyAMA0", "/dev/ttyMT0",
            "/dev/ttySAC0", "/dev/ttyMSM0", "/dev/ttyHSL0", "/dev/ttyHS0");

        File dev = new File("/dev");
        File[] files = dev.listFiles();
        if (files != null) {
            for (File file : files) {
                if (SERIAL_NAME_PATTERN.matcher(file.getName()).matches()) {
                    candidates.add(file.getAbsolutePath());
                }
            }
        } else {
            report.append("无法列出 /dev，系统可能限制普通 APK 访问。\n");
        }

        ArrayList<PortInfo> found = new ArrayList<>();
        for (String path : candidates) {
            File file = new File(path);
            if (!file.exists()) {
                continue;
            }
            PortInfo info = new PortInfo(path, file.canRead(), file.canWrite(), runShell("ls -l " + shellQuote(path)));
            found.add(info);
            report.append(path)
                .append("  read=").append(info.canRead)
                .append("  write=").append(info.canWrite)
                .append('\n');
            if (info.detail.trim().length() > 0) {
                report.append("  ").append(info.detail.trim()).append('\n');
            }
        }

        if (found.isEmpty()) {
            report.append("没有发现常见串口节点。可拍照反馈设备设置页，或让厂商确认 Android 侧串口设备名。\n");
        }
        return found;
    }

    private void testSelectedAsync() {
        if (portPaths.isEmpty()) {
            toast("没有可测试的候选串口");
            return;
        }
        String path = (String) portSpinner.getSelectedItem();
        testPathsAsync(Collections.singletonList(path));
    }

    private void testAllAsync() {
        if (portPaths.isEmpty()) {
            toast("没有可测试的候选串口");
            return;
        }
        testPathsAsync(new ArrayList<>(portPaths));
    }

    private void testPathsAsync(List<String> paths) {
        setBusy(true);
        stateText.setText("正在测试串口…");
        stateText.setTextColor(PRIMARY);
        new Thread(() -> {
            StringBuilder report = new StringBuilder();
            report.append(headerLine("串口测试"));
            boolean anyResponse = false;
            for (String path : paths) {
                ProbeOutcome outcome = testPort(path);
                report.append(outcome.report).append('\n');
                anyResponse = anyResponse || outcome.responseReceived;
            }
            boolean success = anyResponse;
            runOnUiThread(() -> {
                stateText.setText(success ? "收到 RS232 返回包，可以继续集成" : "未收到有效返回包");
                stateText.setTextColor(success ? SUCCESS : DANGER);
                resultText.setText(report.toString());
                setBusy(false);
            });
        }, "serial-probe-test").start();
    }

    private ProbeOutcome testPort(String path) {
        StringBuilder report = new StringBuilder();
        report.append("==== ").append(path).append(" ====\n");
        File file = new File(path);
        report.append("exists=").append(file.exists())
            .append(" read=").append(file.canRead())
            .append(" write=").append(file.canWrite()).append('\n');
        if (!file.exists()) {
            report.append("结果：文件不存在。\n");
            return new ProbeOutcome(report.toString(), false);
        }

        String stty = runShell("stty -F " + shellQuote(path) + " 19200 cs8 -cstopb -parenb raw -echo -ixon -ixoff -crtscts min 0 time 10 2>&1");
        report.append("stty 19200：").append(stty.trim().length() == 0 ? "已执行，无输出" : stty.trim()).append('\n');

        ReadResult protocol = writeAndRead(path, QUERY_PROTOCOL, 1800);
        report.append("发送协议版本查询：").append(bytesToHex(QUERY_PROTOCOL)).append('\n');
        report.append("返回：").append(protocol.describe()).append('\n');
        if (protocol.validFrame) {
            report.append("解析：").append(parseFrame(protocol.bytes)).append('\n');
        }

        ReadResult power = writeAndRead(path, QUERY_POWER, 1800);
        report.append("发送电源状态查询：").append(bytesToHex(QUERY_POWER)).append('\n');
        report.append("返回：").append(power.describe()).append('\n');
        if (power.validFrame) {
            report.append("解析：").append(parseFrame(power.bytes)).append('\n');
        }

        boolean responseReceived = protocol.validFrame || power.validFrame;
        report.append("结论：").append(responseReceived
            ? "收到联想协议返回包，Android 侧可继续做 RS232 控制。"
            : "没有收到 CC...DD 返回包，可能是权限不足、串口名不对、线缆未接到 Android 侧，或该 RS232 只给外部中控使用。").append('\n');
        return new ProbeOutcome(report.toString(), responseReceived);
    }

    private ReadResult writeAndRead(String path, byte[] command, long timeoutMs) {
        ByteArrayOutputStream outputBytes = new ByteArrayOutputStream();
        try (FileInputStream input = new FileInputStream(path);
             FileOutputStream output = new FileOutputStream(path)) {
            drainInput(input);
            output.write(command);
            output.flush();
            long deadline = System.currentTimeMillis() + timeoutMs;
            byte[] buffer = new byte[64];
            while (System.currentTimeMillis() < deadline) {
                int available = input.available();
                if (available > 0) {
                    int read = input.read(buffer, 0, Math.min(buffer.length, available));
                    if (read > 0) {
                        outputBytes.write(buffer, 0, read);
                        byte[] bytes = outputBytes.toByteArray();
                        if (bytes.length > 0 && bytes[bytes.length - 1] == (byte) 0xDD) {
                            break;
                        }
                    }
                }
                Thread.sleep(50);
            }
            byte[] response = outputBytes.toByteArray();
            return new ReadResult(response, isValidFrame(response), null);
        } catch (Throwable ex) {
            return new ReadResult(outputBytes.toByteArray(), isValidFrame(outputBytes.toByteArray()), ex);
        }
    }

    private void drainInput(FileInputStream input) {
        try {
            byte[] buffer = new byte[128];
            int available = input.available();
            while (available > 0) {
                int read = input.read(buffer, 0, Math.min(buffer.length, available));
                if (read <= 0) {
                    return;
                }
                available = input.available();
            }
        } catch (Throwable ignored) {
        }
    }

    private static boolean isValidFrame(byte[] bytes) {
        return bytes.length >= 6 && bytes[0] == (byte) 0xCC && bytes[bytes.length - 1] == (byte) 0xDD;
    }

    private static String parseFrame(byte[] bytes) {
        if (!isValidFrame(bytes)) {
            return "不是完整 CC...DD 返回包";
        }
        int result = unsigned(bytes[1]);
        int page = unsigned(bytes[2]);
        int id = unsigned(bytes[3]);
        int length = unsigned(bytes[4]);
        String resultText;
        if (result == 0) {
            resultText = "成功";
        } else if (result == 1) {
            resultText = "失败";
        } else if (result == 2) {
            resultText = "不支持命令";
        } else {
            resultText = "错误码 " + result;
        }
        String extra = "";
        if (length > 0 && bytes.length >= 6 + length) {
            byte[] data = new byte[length];
            System.arraycopy(bytes, 5, data, 0, length);
            extra = " 附加数据=" + bytesToHex(data);
            if (page == 0x24 && id == 0x0E && length == 2) {
                extra += " 协议版本=" + unsigned(data[0]) + "." + unsigned(data[1]);
            }
            if (page == 0x24 && id == 0x01 && length >= 1) {
                extra += unsigned(data[0]) == 0 ? " 电源状态=正常" : " 电源状态=节能/待机";
            }
        }
        return "Result=" + resultText + " Page=" + toHexByte(page) + " ID=" + toHexByte(id) + " Length=" + length + extra;
    }

    private String getUsbSummary() {
        StringBuilder report = new StringBuilder();
        report.append("USB 设备：\n");
        try {
            UsbManager manager = (UsbManager) getSystemService(Context.USB_SERVICE);
            if (manager == null) {
                report.append("  无法读取 UsbManager。\n");
                return report.toString();
            }
            Map<String, UsbDevice> devices = manager.getDeviceList();
            if (devices.isEmpty()) {
                report.append("  未发现外接 USB 设备。\n");
                return report.toString();
            }
            for (UsbDevice device : devices.values()) {
                report.append("  ")
                    .append(device.getDeviceName())
                    .append(" vid=").append(toHexWord(device.getVendorId()))
                    .append(" pid=").append(toHexWord(device.getProductId()))
                    .append(" class=").append(device.getDeviceClass())
                    .append('\n');
            }
            report.append("  注：本检测版会列出 USB 转串口设备，但默认不通过 USB Host 协议收发串口数据。\n");
        } catch (Throwable ex) {
            report.append("  读取 USB 设备失败：").append(ex.getMessage()).append('\n');
        }
        return report.toString();
    }

    private String runShell(String command) {
        try {
            Process process = new ProcessBuilder("sh", "-c", command).redirectErrorStream(true).start();
            ByteArrayOutputStream output = new ByteArrayOutputStream();
            try (InputStream input = process.getInputStream()) {
                byte[] buffer = new byte[256];
                int read;
                while ((read = input.read(buffer)) >= 0) {
                    output.write(buffer, 0, read);
                }
            }
            process.waitFor();
            return output.toString(StandardCharsets.UTF_8.name());
        } catch (Throwable ex) {
            return ex.getClass().getSimpleName() + ": " + ex.getMessage();
        }
    }

    private void setBusy(boolean busy) {
        if (scanButton != null) {
            scanButton.setEnabled(!busy);
        }
        if (testSelectedButton != null) {
            testSelectedButton.setEnabled(!busy);
        }
        if (testAllButton != null) {
            testAllButton.setEnabled(!busy);
        }
    }

    private void copyResult() {
        ClipboardManager clipboard = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
        if (clipboard != null) {
            clipboard.setPrimaryClip(ClipData.newPlainText("RS232 检测结果", resultText.getText()));
            toast("检测结果已复制");
        }
    }

    private LinearLayout card() {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(20), dp(18), dp(20), dp(18));
        card.setBackground(round(SURFACE, Color.rgb(225, 232, 240), 20, 1));
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            LinearLayout.LayoutParams.WRAP_CONTENT);
        params.setMargins(0, 0, 0, dp(14));
        card.setLayoutParams(params);
        return card;
    }

    private TextView sectionTitle(String value) {
        TextView textView = text(value, 17, PRIMARY_TEXT, true);
        textView.setPadding(0, 0, 0, dp(10));
        return textView;
    }

    private LinearLayout buttonRow(Button left, Button right) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setPadding(0, dp(10), 0, 0);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(54), 1);
        lp.setMargins(0, 0, dp(8), 0);
        row.addView(left, lp);
        LinearLayout.LayoutParams rp = new LinearLayout.LayoutParams(0, dp(54), 1);
        rp.setMargins(dp(8), 0, 0, 0);
        row.addView(right, rp);
        return row;
    }

    private Button blueButton(String text, View.OnClickListener listener) {
        Button button = button(text, Color.WHITE, PRIMARY, PRIMARY, listener);
        if ("重新扫描".equals(text)) {
            scanButton = button;
        }
        return button;
    }

    private Button whiteButton(String text, View.OnClickListener listener) {
        Button button = button(text, PRIMARY_TEXT, SURFACE, Color.rgb(203, 213, 225), listener);
        if ("测试选中串口".equals(text)) {
            testSelectedButton = button;
        } else if ("测试全部串口".equals(text)) {
            testAllButton = button;
        }
        return button;
    }

    private Button button(String text, int textColor, int fillColor, int strokeColor, View.OnClickListener listener) {
        Button button = new Button(this);
        button.setText(text);
        button.setTextSize(14);
        button.setTypeface(Typeface.DEFAULT_BOLD);
        button.setTextColor(textColor);
        button.setAllCaps(false);
        button.setMaxLines(2);
        button.setPadding(dp(6), 0, dp(6), 0);
        button.setGravity(Gravity.CENTER);
        button.setBackground(round(fillColor, strokeColor, 12, 1));
        button.setOnClickListener(listener);
        return button;
    }

    private TextView text(String value, int sp, int color, boolean bold) {
        TextView textView = new TextView(this);
        textView.setText(value);
        textView.setTextSize(sp);
        textView.setTextColor(color);
        if (bold) {
            textView.setTypeface(Typeface.DEFAULT_BOLD);
        }
        return textView;
    }

    private GradientDrawable round(int fillColor, int strokeColor, int radiusDp, int strokeDp) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fillColor);
        drawable.setCornerRadius(dp(radiusDp));
        drawable.setStroke(dp(strokeDp), strokeColor);
        return drawable;
    }

    private int contentWidth() {
        float density = getResources().getDisplayMetrics().density;
        int widthDp = (int) (getResources().getDisplayMetrics().widthPixels / density);
        return widthDp >= 920 ? dp(900) : FrameLayout.LayoutParams.MATCH_PARENT;
    }

    private static String headerLine(String title) {
        return "==== " + title + " " + new SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.CHINA).format(new Date()) + " ====\n";
    }

    private static String shellQuote(String value) {
        return "'" + value.replace("'", "'\"'\"'") + "'";
    }

    private static byte[] hex(String value) {
        String[] parts = value.trim().split("\\s+");
        byte[] bytes = new byte[parts.length];
        for (int i = 0; i < parts.length; i++) {
            bytes[i] = (byte) Integer.parseInt(parts[i], 16);
        }
        return bytes;
    }

    private static String bytesToHex(byte[] bytes) {
        if (bytes == null || bytes.length == 0) {
            return "无";
        }
        StringBuilder builder = new StringBuilder();
        for (byte value : bytes) {
            if (builder.length() > 0) {
                builder.append(' ');
            }
            builder.append(toHexByte(unsigned(value)));
        }
        return builder.toString();
    }

    private static int unsigned(byte value) {
        return value & 0xFF;
    }

    private static String toHexByte(int value) {
        String hex = Integer.toHexString(value & 0xFF).toUpperCase(Locale.ROOT);
        return hex.length() == 1 ? "0" + hex : hex;
    }

    private static String toHexWord(int value) {
        String hex = Integer.toHexString(value & 0xFFFF).toUpperCase(Locale.ROOT);
        while (hex.length() < 4) {
            hex = "0" + hex;
        }
        return "0x" + hex;
    }

    private int dp(int value) {
        return (int) (value * getResources().getDisplayMetrics().density + 0.5f);
    }

    private void toast(String value) {
        Toast.makeText(this, value, Toast.LENGTH_SHORT).show();
    }

    private static final class PortInfo {
        final String path;
        final boolean canRead;
        final boolean canWrite;
        final String detail;

        PortInfo(String path, boolean canRead, boolean canWrite, String detail) {
            this.path = path;
            this.canRead = canRead;
            this.canWrite = canWrite;
            this.detail = detail == null ? "" : detail;
        }
    }

    private static final class ProbeOutcome {
        final String report;
        final boolean responseReceived;

        ProbeOutcome(String report, boolean responseReceived) {
            this.report = report;
            this.responseReceived = responseReceived;
        }
    }

    private static final class ReadResult {
        final byte[] bytes;
        final boolean validFrame;
        final Throwable error;

        ReadResult(byte[] bytes, boolean validFrame, Throwable error) {
            this.bytes = bytes == null ? new byte[0] : bytes;
            this.validFrame = validFrame;
            this.error = error;
        }

        String describe() {
            String text = bytesToHex(bytes);
            if (error != null) {
                text += "；错误=" + error.getClass().getSimpleName() + ": " + error.getMessage();
            }
            if (bytes.length == 0 && error == null) {
                text = "超时未收到数据";
            }
            return text;
        }
    }
}
