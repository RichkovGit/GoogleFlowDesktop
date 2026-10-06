"""
PyInstaller build script to create standalone GoogleFlow.exe
"""
import os
import subprocess
import sys

def build():
    current_dir = os.path.dirname(os.path.abspath(__file__))
    ui_dir = os.path.join(current_dir, "ui")
    
    cmd = [
        sys.executable, "-m", "PyInstaller",
        "--name=GoogleFlow",
        "--onedir",
        "--noconsole",
        f"--add-data={ui_dir};ui",
        "--hidden-import=pythonnet",
        "--hidden-import=clr",
        "--hidden-import=webview",
        "--hidden-import=PIL",
        "--hidden-import=psutil",
        os.path.join(current_dir, "app.py")
    ]
    
    print("Running PyInstaller:", " ".join(cmd))
    subprocess.run(cmd, check=True)
    print("\n[Build Complete] Standalone binary is available in dist/GoogleFlow/GoogleFlow.exe")

if __name__ == "__main__":
    build()
