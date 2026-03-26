import { useCallback, useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import "./App.css";

type NetConfiguredRole = "none" | "host" | "client";

type NetRoleResponse = {
  role: NetConfiguredRole;
};

type NetStatus = {
  configuredRole: NetConfiguredRole;
  state: string;
  thisHostIp: string | null;
  remoteHostIp: string | null;
  remoteTcpPort: number | null;
  remoteHostBaseUrl: string | null;
  lanPort: number;
  udpPort: number;
  appId: string;
};

type Book = {
  id: number;
  title: string;
  author: string;
  yearPublished: number;
};

function roleLabel(role: NetConfiguredRole): string {
  switch (role) {
    case "host":
      return "хост (beacon из appsettings)";
    case "client":
      return "клиент (поиск хоста из appsettings)";
    default:
      return "выкл. (Role: none в appsettings)";
  }
}

export default function App() {
  const [baseUrl, setBaseUrl] = useState<string | null>(null);
  const [configuredRole, setConfiguredRole] = useState<NetConfiguredRole | null>(null);
  const [net, setNet] = useState<NetStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [books, setBooks] = useState<Book[] | null>(null);
  const [booksLoading, setBooksLoading] = useState(false);
  const [booksError, setBooksError] = useState<string | null>(null);

  useEffect(() => {
    void invoke<string>("get_backend_base_url").then(setBaseUrl).catch((e) => {
      setError(String(e));
    });
  }, []);

  const fetchRole = useCallback(async () => {
    if (!baseUrl) return;
    const r = await fetch(`${baseUrl}/api/net/role`);
    if (!r.ok) throw new Error(`role ${r.status}`);
    const data = (await r.json()) as NetRoleResponse;
    setConfiguredRole(data.role);
  }, [baseUrl]);

  const fetchStatus = useCallback(async () => {
    if (!baseUrl) return;
    const r = await fetch(`${baseUrl}/api/net/status`);
    if (!r.ok) throw new Error(`status ${r.status}`);
    setNet((await r.json()) as NetStatus);
  }, [baseUrl]);

  useEffect(() => {
    if (!baseUrl) return;
    void fetchRole().catch((e) => setError(String(e)));
  }, [baseUrl, fetchRole]);

  useEffect(() => {
    if (!baseUrl) return;
    void fetchStatus();
    const id = window.setInterval(() => void fetchStatus().catch(() => {}), 1000);
    return () => window.clearInterval(id);
  }, [baseUrl, fetchStatus]);

  async function refreshPageInfo() {
    if (!baseUrl) return;
    setRefreshing(true);
    setError(null);
    try {
      await Promise.all([fetchRole(), fetchStatus()]);
    } catch (e) {
      setError(String(e));
    } finally {
      setRefreshing(false);
    }
  }

  async function fetchBooks() {
    if (!baseUrl) return;
    setBooksLoading(true);
    setBooksError(null);
    try {
      const r = await fetch(`${baseUrl}/api/Books`);
      if (!r.ok) throw new Error(`книги: HTTP ${r.status}`);
      const data = (await r.json()) as Book[];
      setBooks(data);
    } catch (e) {
      setBooksError(String(e));
      setBooks(null);
    } finally {
      setBooksLoading(false);
    }
  }

  return (
    <main className="container">
      <h1>Сеть: хост / клиент</h1>

      {!baseUrl && <p className="muted">Загрузка backend…</p>}
      {error && <p className="error">{error}</p>}

      <section className="card">
        <div className="row card-header-row">
          <h2>Режим из конфигурации</h2>
          <button
            type="button"
            className="btn-refresh"
            disabled={!baseUrl || refreshing}
            onClick={() => void refreshPageInfo()}
          >
            {refreshing ? "Обновление…" : "Обновить"}
          </button>
        </div>
        {configuredRole == null ? (
          <p className="muted">Запрос /api/net/role…</p>
        ) : (
          <>
            <p>
              <strong>{roleLabel(configuredRole)}</strong>
            </p>
            <p className="hint">
              Меняется только в <code>appsettings.json</code> → <code>Net:Role</code> (<code>none</code>,{" "}
              <code>host</code>, <code>client</code>), затем перезапуск процесса backend.
            </p>
          </>
        )}
      </section>

      <section className="card">
        <div className="row card-header-row">
          <h2>Тест API: книги</h2>
          <button
            type="button"
            className="btn-refresh"
            disabled={!baseUrl || booksLoading}
            onClick={() => void fetchBooks()}
          >
            {booksLoading ? "Загрузка…" : "Получить книги"}
          </button>
        </div>
        {booksError && <p className="error">{booksError}</p>}
        {books === null && !booksError && (
          <p className="muted">Нажмите кнопку, чтобы запросить <code>GET /api/Books</code>.</p>
        )}
        {books && books.length > 0 && (
          <table className="books-table">
            <thead>
              <tr>
                <th>ID</th>
                <th>Название</th>
                <th>Автор</th>
                <th>Год</th>
              </tr>
            </thead>
            <tbody>
              {books.map((b) => (
                <tr key={b.id}>
                  <td>{b.id}</td>
                  <td>{b.title}</td>
                  <td>{b.author}</td>
                  <td>{b.yearPublished}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        {books && books.length === 0 && <p className="muted">Список пуст.</p>}
      </section>

      {net && (
        <section className="card status">
          <h2>Статус discovery</h2>
          <dl>
            <dt>configuredRole (из конфига)</dt>
            <dd>{net.configuredRole}</dd>
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
