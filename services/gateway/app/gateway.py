"""AI-ACE 网关: 唯一对外(宿主机)出口。
只做 TCP 转发到固定上游, 不做任何数据外发; 上游全部在内网 ai-private-net。
MAP 格式: "listen_port:upstream_host:upstream_port" 多条, 用 ; 分隔。
"""
import asyncio, os, sys

async def pipe(reader, writer):
    try:
        while True:
            data = await reader.read(65536)
            if not data:
                break
            writer.write(data)
            await writer.drain()
    except Exception:
        pass
    finally:
        try:
            writer.close()
        except Exception:
            pass

async def handle(client_reader, client_writer, host, port):
    try:
        up_reader, up_writer = await asyncio.open_connection(host, port)
    except Exception as e:
        print(f"[gateway] upstream {host}:{port} unreachable: {e}", flush=True)
        client_writer.close()
        return
    await asyncio.gather(pipe(client_reader, up_writer), pipe(up_reader, client_writer))

async def serve(listen_port, host, port):
    server = await asyncio.start_server(
        lambda r, w: handle(r, w, host, port), "0.0.0.0", listen_port)
    print(f"[gateway] :{listen_port} -> {host}:{port}", flush=True)
    async with server:
        await server.serve_forever()

async def main():
    raw = os.getenv("MAP", "3000:open-webui:8080;9000:ace-dashboard:9000")
    tasks = []
    for item in raw.split(";"):
        item = item.strip()
        if not item:
            continue
        lp, host, port = item.split(":")
        tasks.append(asyncio.create_task(serve(int(lp), host, int(port))))
    if not tasks:
        print("no MAP configured", flush=True); sys.exit(1)
    await asyncio.gather(*tasks)

if __name__ == "__main__":
    asyncio.run(main())
