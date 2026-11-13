// Records the compiler version, which the worker reports to the dashboard as its runtime.
use std::process::Command;

fn main() {
    let rustc = std::env::var("RUSTC").unwrap_or_else(|_| "rustc".to_string());

    let version = Command::new(rustc)
        .arg("--version")
        .output()
        .ok()
        .and_then(|output| String::from_utf8(output.stdout).ok())
        .and_then(|text| text.split_whitespace().nth(1).map(str::to_string))
        .unwrap_or_else(|| "unknown".to_string());

    println!("cargo:rustc-env=MILVAION_RUSTC_VERSION={version}");
    println!("cargo:rerun-if-env-changed=RUSTC");
}
