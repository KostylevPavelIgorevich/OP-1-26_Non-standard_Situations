use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::time::Duration;
use tauri::Manager;

const DOTNET_BACKEND_URL: &str = "http://127.0.0.1:5188";

struct DotnetBackendState(Mutex<Option<Child>>);

#[tauri::command]
async fn greet(name: String) -> Result<String, String> {
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(3))
        .build()
        .map_err(|e| format!("Failed to create HTTP client: {e}"))?;

    let response = client
        .get(format!("{DOTNET_BACKEND_URL}/greet"))
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

fn spawn_dotnet_backend() -> Result<Child, String> {
    let mut command = Command::new("dotnet");
    command
        .arg("run")
        .arg("--project")
        .arg("../src-dotnet")
        .arg("--")
        .arg("--urls")
        .arg(DOTNET_BACKEND_URL)
        .stdout(Stdio::null())
        .stderr(Stdio::null());

    command
        .spawn()
        .map_err(|e| format!("Failed to start .NET backend. Make sure .NET SDK is installed: {e}"))
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            let child = spawn_dotnet_backend()?;
            app.manage(DotnetBackendState(Mutex::new(Some(child))));
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![greet])
        .build(tauri::generate_context!())
        .expect("error while building tauri application");

    app.run(|app_handle, event| {
        if let tauri::RunEvent::Exit = event {
            let state: tauri::State<DotnetBackendState> = app_handle.state();
            let lock_result = state.0.lock();
            if let Ok(mut child_guard) = lock_result {
                if let Some(child) = child_guard.as_mut() {
                    let _ = child.kill();
                }
            }
        }
    });
}
