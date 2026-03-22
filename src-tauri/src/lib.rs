use std::net::TcpListener;
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::thread;
use std::time::Duration;
use tauri::Manager;

/// Состояние: URL локального C# backend и дочерний процесс (если запущен).
struct DotnetBackendState {
    base_url: String,
    child: Mutex<Option<Child>>,
}

fn pick_free_port() -> Result<u16, String> {
    let listener = TcpListener::bind("127.0.0.1:0")
        .map_err(|e| format!("Failed to bind to pick free port: {e}"))?;

    let port = listener
        .local_addr()
        .map_err(|e| format!("Failed to get local address: {e}"))?
        .port();

    drop(listener);
    Ok(port)
}

/// Ждём, пока Kestrel ответит на /health (после spawn dotnet).
fn wait_for_health(base_url: &str) -> Result<(), String> {
    let client = reqwest::blocking::Client::builder()
        .timeout(Duration::from_millis(500))
        .build()
        .map_err(|e| format!("blocking HTTP client: {e}"))?;

    for attempt in 0..60 {
        let url = format!("{base_url}/health");
        match client.get(&url).send() {
            Ok(resp) if resp.status().is_success() => return Ok(()),
            _ => {
                if attempt == 59 {
                    return Err(
                        ".NET backend did not respond on /health in time. Is dotnet SDK installed?"
                            .into(),
                    );
                }
                thread::sleep(Duration::from_millis(100));
            }
        }
    }
    Ok(())
}

#[tauri::command]
async fn greet(
    name: String,
    state: tauri::State<'_, DotnetBackendState>,
) -> Result<String, String> {
    let base_url = state.base_url.clone();
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(3))
        .build()
        .map_err(|e| format!("Failed to create HTTP client: {e}"))?;

    let response = client
        .get(format!("{base_url}/greet"))
        .query(&[("name", name)])
        .send()
        .await
        .map_err(|e| format!("Failed to call .NET backend: {e}"))?;

    if !response.status().is_success() {
        return Err(format!(
            ".NET backend returned unexpected status: {}",
            response.status()
        ));
    }

    response
        .text()
        .await
        .map_err(|e| format!("Failed to read .NET response: {e}"))
}

/// Отдаёт базовый URL C# API (для прямых fetch из React).
#[tauri::command]
fn get_backend_base_url(state: tauri::State<'_, DotnetBackendState>) -> String {
    state.base_url.clone()
}

fn spawn_dotnet_backend(base_url: &str) -> Result<Child, String> {
    let mut command = Command::new("dotnet");
    command
        .arg("run")
        .arg("--project")
        .arg("../src-dotnet")
        .arg("--")
        .arg("--urls")
        .arg(base_url)
        .stdout(Stdio::null())
        .stderr(Stdio::null());

    command
        .spawn()
        .map_err(|e| format!("Failed to start .NET backend. Is .NET SDK installed? {e}"))
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            let port = pick_free_port()?;
            let base_url = format!("http://127.0.0.1:{port}");

            let child = spawn_dotnet_backend(&base_url)?;
            wait_for_health(&base_url)?;

            app.manage(DotnetBackendState {
                base_url,
                child: Mutex::new(Some(child)),
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![greet, get_backend_base_url])
        .build(tauri::generate_context!())
        .expect("error while building tauri application");

    app.run(|app_handle, event| {
        if let tauri::RunEvent::Exit = event {
            let state: tauri::State<DotnetBackendState> = app_handle.state();
            let lock_result = state.child.lock();
            if let Ok(mut child_guard) = lock_result {
                if let Some(child) = child_guard.as_mut() {
                    let _ = child.kill();
                }
            }
        }
    });
}
