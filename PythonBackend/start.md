# start.ps1

Launches `server.py` and `diagnostic.py` in sequence.

## Usage

```powershell
.\start.ps1                       # host read from config.SERVER_HOST
.\start.ps1 -ServerHost <ip>      # override server host
```

## Sequence

1. Starts `server.py` in a new console window (logs visible separately)
2. Waits 6 seconds for the YOLO model to load and WebSocket ports to bind
3. Opens `diagnostic.py` in the current window
4. On diagnostic exit, prompts to stop the server process

## Notes

- Motor initialisation is async — the diagnostic will show PTZ as "not ready" for a few seconds after startup; this is normal.
- The server window stays open independently so its logs can be monitored while using the diagnostic.
- If the server host differs from `config.py SERVER_HOST`, pass `-ServerHost` to keep the diagnostic pointed at the same machine.
