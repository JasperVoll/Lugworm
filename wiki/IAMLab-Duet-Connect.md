# IAMLab Duet Connect

IAMLab Duet Connect opens the connection to a printer with a Duet
board over WiFi. The `host` output identifies the connection and is
connected to every other Duet component. Connect and Disconnect are
buttons on the component; reopening a saved file never connects on its
own.

Panel: `7 Print`. Nickname: `Duet`. Compute button: no (runs on every change).

<!-- IMAGE TODO: Duet Connect with the host and password inputs and a panel on the status output. A short screen recording saved as GIF can replace the image. -->
![IAMLab Duet Connect on the canvas](images/comp-duet-connect.png)

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `host` | Text | 192.168.1.42 | Duet hostname or IP (e.g. 192.168.1.42) |
| `password` | Text |  | Duet password (empty = firmware default 'reprap'). Stored as plaintext in the .gh file and transported as plaintext HTTP on the LAN; intentional spaces are preserved. |
| `pollMs` | Integer | 1000 | Status poll interval, ms (250–4000) |

## Outputs

| Name | Type | Description |
|---|---|---|
| `host` | Text | Host string identifying this session - connect it to the other Duet components |
| `connected` | Boolean | True while the session is live |
| `status` | Text | Machine status (idle / processing / halted / …) |
| `info` | Text | Session diagnostics |

## Notes

- The password is stored in plain text in the `.gh` file and sent in
  plain text over the local network. Clear the password before sharing
  a file.
- On the first connection, the component remembers which board
  answered. If a different board answers later at the same address,
  actions are blocked until the new board is accepted with "Trust
  connected machine" in the context menu of the component.

Back to [[Components]].
