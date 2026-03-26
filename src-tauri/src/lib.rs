use std::net::TcpListener;
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::thread;
use std::time::Duration;
use tauri::Manager;

/// Путь к уже собранному исполняемому файлу дочернего процесса (задаётся снаружи, напр. из launch.json).
const ENV_CHILD_EXE: &str = "BACKEND_EXECUTABLE";
/// Базовый HTTP URL, на котором дочерний процесс должен слушать (читает сам процесс — формат на его стороне).
const ENV_CHILD_HTTP_BASE: &str = "BACKEND_HTTP_BASE_URL";

struct LocalChildState {
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
                    return Err(format!(
                        "Child process did not respond on {url} in time."
                    ));
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
    state: tauri::State<'_, LocalChildState>,
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
        .map_err(|e| format!("Failed to call HTTP service: {e}"))?;

    if !response.status().is_success() {
        return Err(format!(
            "HTTP service returned unexpected status: {}",
            response.status()
        ));
    }

    response
        .text()
        .await
        .map_err(|e| format!("Failed to read response: {e}"))
}

#[tauri::command]
fn get_backend_base_url(state: tauri::State<'_, LocalChildState>) -> String {
    state.base_url.clone()
}

fn resolve_child_executable() -> Result<PathBuf, String> {
    std::env::var(ENV_CHILD_EXE)
        .map_err(|_| {
            format!(
                "Environment variable {ENV_CHILD_EXE} is not set. \
                 Point it at your built server binary (e.g. VS Code launch + preLaunchTask)."
            )
        })
        .map(PathBuf::from)
        .and_then(|p| {
            if p.is_file() {
                Ok(p)
            } else {
                Err(format!(
                    "{ENV_CHILD_EXE} does not refer to a file: {}",
                    p.display()
                ))
            }
        })
}

fn spawn_child_process(base_url: &str) -> Result<Child, String> {
    let exe_path = resolve_child_executable()?;
    let workdir = exe_path
        .parent()
        .ok_or_else(|| "executable path has no parent directory".to_string())?
        .to_path_buf();

    Command::new(&exe_path)
        .current_dir(workdir)
        .env(ENV_CHILD_HTTP_BASE, base_url)
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| format!("Failed to spawn {}: {e}", exe_path.display()))
}

fn terminate_child(state: &LocalChildState) {
    let Ok(mut guard) = state.child.lock() else {
        return;
    };
    let Some(mut child) = guard.take() else {
        return;
    };
    let _ = child.kill();
    let _ = child.wait();
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .setup(|_app| {
            let port = pick_free_port()?;
            let base_url = format!("http://127.0.0.1:{port}");

            let child = spawn_child_process(&base_url)?;
            wait_for_health(&base_url)?;

            _app.manage(LocalChildState {
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
            let state: tauri::State<LocalChildState> = app_handle.state();
            terminate_child(&state);
        }
    });
}
