# 在 open-webui 容器内运行: 验证容器是否真的出不了网。
# 用法(宿主机): docker cp compose\offline_probe.py open-webui:/tmp/ && docker exec open-webui python /tmp/offline_probe.py
# 退出码: 0 = 已断网(合规), 3 = 仍可出网(不合规)
import socket
import sys


def probe(host, port, label):
    s = socket.socket()
    s.settimeout(5)
    try:
        s.connect((host, port))
        print(f'{label}: REACHABLE')
        return True
    except Exception as exc:
        print(f'{label}: BLOCKED ({type(exc).__name__})')
        return False
    finally:
        s.close()


online = False
online |= probe('1.1.1.1', 443, 'raw-ip-1.1.1.1')
online |= probe('223.5.5.5', 443, 'raw-ip-223.5.5.5')

for name in ('huggingface.co', 'api.openai.com', 'pypi.org', 'api.openwebui.com'):
    try:
        socket.getaddrinfo(name, 443)
        print(f'dns-{name}: RESOLVED')
        online = True
    except Exception as exc:
        print(f'dns-{name}: FAILED ({type(exc).__name__})')

print('RESULT: ONLINE' if online else 'RESULT: OFFLINE')
sys.exit(3 if online else 0)
