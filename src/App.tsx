import { useCallback, useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import "./App.css";

type NetStatus = {
  state: string;
  thisHostIp: string | null;
  remoteHostIp: string | null;
  remoteTcpPort: number | null;
  remoteHostBaseUrl: string | null;
  lanPort: number;
  udpPort: number;
  appId: string;
};

export default function App() {
  const [baseUrl, setBaseUrl] = useState<string | null>(null);
  const [net, setNet] = useState<NetStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    void invoke<string>("get_backend_base_url").then(setBaseUrl).catch((e) => {
      setError(String(e));
    });
  }, []);

  const fetchStatus = useCallback(async () => {
    if (!baseUrl) return;
    const r = await fetch(`${baseUrl}/api/net/status`);
    if (!r.ok) throw new Error(`status ${r.status}`);
    setNet((await r.json()) as NetStatus);
  }, [baseUrl]);

  useEffect(() => {
    if (!baseUrl) return;
    void fetchStatus();
    const id = window.setInterval(() => void fetchStatus().catch(() => {}), 1000);
    return () => window.clearInterval(id);
  }, [baseUrl, fetchStatus]);

  async function startMode(mode: "host" | "client") {
    if (!baseUrl) return;
    setBusy(true);
    setError(null);
    try {
      const r = await fetch(`${baseUrl}/api/net/start`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ mode }),
      });
      if (!r.ok) {
        const t = await r.text();
        throw new Error(t || `HTTP ${r.status}`);
      }
      setNet((await r.json()) as NetStatus);
    } catch (e) {
      setError(String(e));
    } finally {
      setBusy(false);
    }
  }

  async function stopNet() {
    if (!baseUrl) return;
    setBusy(true);
    setError(null);
    try {
      const r = await fetch(`${baseUrl}/api/net/stop`, { method: "POST" });
      if (!r.ok) throw new Error(`HTTP ${r.status}`);
      setNet((await r.json()) as NetStatus);
    } catch (e) {
      setError(String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="container">
      <h1>Сеть: хост / клиент</h1>

      {!baseUrl && <p className="muted">Загрузка backend…</p>}
      {error && <p className="error">{error}</p>}

      <section className="card">
        <h2>Режим</h2>
        <div className="row">
          <button type="button" disabled={!baseUrl || busy} onClick={() => void startMode("host")}>
            Хост (UDP beacon)
          </button>
          <button type="button" disabled={!baseUrl || busy} onClick={() => void startMode("client")}>
            Клиент (поиск хоста)
          </button>
          <button type="button" disabled={!baseUrl || busy} onClick={() => void stopNet()}>
            Стоп
          </button>
        </div>
        <p className="hint">
          Клиент ~5 с ищет хост по UDP; если не найден — <strong>clientLocalOnly</strong> (локально на этом ПК).
        </p>
      </section>

      {net && (
        <section className="card status">
          <h2>Статус</h2>
          <dl>
            <dt>state</dt>
            <dd>{net.state}</dd>
            <dt>thisHostIp</dt>
            <dd>{net.thisHostIp ?? "—"}</dd>
            <dt>remoteHostBaseUrl</dt>
            <dd>{net.remoteHostBaseUrl ?? "—"}</dd>
            <dt>UDP / LAN порты</dt>
            <dd>
              {net.udpPort} / {net.lanPort}
            </dd>
            <dt>appId</dt>
            <dd>{net.appId}</dd>
          </dl>
        </section>
      )}
    </main>
  );
}
