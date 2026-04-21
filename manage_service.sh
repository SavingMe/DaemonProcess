#!/bin/bash

# 检查是否以 root/sudo 权限运行
if [ "$EUID" -ne 0 ]; then
  echo "❌ 错误: 配置系统服务需要 root 权限。"
  echo "💡 请使用 sudo 运行此脚本 (例如: sudo ./manage_service.sh)"
  exit 1
fi

function install_service() {
    echo ""
    echo "=== 📝 填写服务配置信息 ==="
    
    # 1. 输入服务名
    read -p "1. 请输入服务名称 (英文字母/数字，不带 .service，例如 myapp): " SERVICE_NAME
    if [ -z "$SERVICE_NAME" ]; then echo "❌ 服务名称不能为空！返回主菜单。"; return; fi

    # 2. 输入 dotnet 路径 (提供默认值)
    read -p "2. 请输入 dotnet 可执行文件的绝对路径 [默认: /usr/bin/dotnet]: " DOTNET_PATH
    DOTNET_PATH=${DOTNET_PATH:-/usr/bin/dotnet}

    # 3. 输入工作目录
    read -p "3. 请输入程序的工作目录 (例如 /var/www/myapp): " WORK_DIR
    if [ -z "$WORK_DIR" ]; then echo "❌ 工作目录不能为空！返回主菜单。"; return; fi

    # 4. 输入 DLL 路径
    read -p "4. 请输入 DLL 文件的绝对路径 (例如 /var/www/myapp/xxx.dll): " DLL_PATH
    if [ -z "$DLL_PATH" ]; then echo "❌ DLL 路径不能为空！返回主菜单。"; return; fi

    SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"

    echo ""
    echo "⏳ 正在生成服务配置文件: $SERVICE_FILE ..."

    # 动态生成 systemd 配置文件
    cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=.NET Core Service for $SERVICE_NAME
After=network.target

[Service]
WorkingDirectory=$WORK_DIR
ExecStart=$DOTNET_PATH $DLL_PATH
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=$SERVICE_NAME

[Install]
WantedBy=multi-user.target
EOF

    echo "⏳ 重新加载 systemd 守护进程..."
    systemctl daemon-reload

    echo "⏳ 设置开机自启并启动服务..."
    systemctl enable "$SERVICE_NAME.service"
    systemctl start "$SERVICE_NAME.service"

    echo "✅ 安装并启动完成！服务当前状态如下："
    echo "------------------------------------------------"
    systemctl status "$SERVICE_NAME.service" --no-pager | head -n 6
    echo "------------------------------------------------"
    echo "💡 提示: 如果需要查看实时日志，请运行: sudo journalctl -fu $SERVICE_NAME"
}

function uninstall_service() {
    echo ""
    echo "=== 🗑️ 填写卸载信息 ==="
    read -p "请输入你要卸载的服务名称 (例如 myapp): " SERVICE_NAME
    if [ -z "$SERVICE_NAME" ]; then echo "❌ 服务名称不能为空！返回主菜单。"; return; fi

    SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"

    # 检查服务文件是否存在
    if [ ! -f "$SERVICE_FILE" ]; then
        echo "⚠️ 服务文件 $SERVICE_FILE 不存在，请检查服务名称是否拼写正确。"
        return
    fi

    echo "⏳ 正在停止服务..."
    systemctl stop "$SERVICE_NAME.service"

    echo "⏳ 正在取消开机自启..."
    systemctl disable "$SERVICE_NAME.service"

    echo "⏳ 删除服务配置文件: $SERVICE_FILE"
    rm -f "$SERVICE_FILE"

    echo "⏳ 重新加载 systemd 守护进程..."
    systemctl daemon-reload

    echo "✅ 卸载清理完成！"
}

# 循环显示主菜单
while true; do
    echo ""
    echo "==================================="
    echo "   .NET Core 动态服务管理脚本"
    echo "==================================="
    echo "请选择你要执行的操作:"
    echo "  1) 一键安装并启动服务"
    echo "  2) 一键卸载并清理服务"
    echo "  3) 退出"
    echo "==================================="
    read -p "请输入选项 [1-3]: " choice

    case $choice in
        1)
            install_service
            ;;
        2)
            uninstall_service
            ;;
        3)
            echo "已退出。"
            exit 0
            ;;
        *)
            echo "❌ 无效的选项，请重新输入。"
            ;;
    esac
done
