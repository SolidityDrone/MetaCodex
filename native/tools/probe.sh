# inside MetaCodex's Linux: what this environment can do towards installing an APK
echo "whoami: $(id)"; echo "arch: $(uname -m)"; . /etc/os-release; echo "os: $PRETTY_NAME"
ls -la /root/native/
command -v adb && adb version | head -1 || echo "adb: not installed"
command -v pm || echo "pm: none in Linux (Android's own is /system/bin/pm)"
ls -la /system/bin/pm 2>/dev/null || echo "/system not visible here"
(exec 3<>/dev/tcp/127.0.0.1/5555 && echo "adbd on 127.0.0.1:5555: reachable") 2>/dev/null || echo "adbd on 127.0.0.1:5555: not reachable"
