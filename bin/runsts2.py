import sys
import subprocess
from pathlib import Path

def main():
    if len(sys.argv) < 2:
        print("Usage: python run_fastmp.py <num_players>")
        sys.exit(1)

    num_players = int(sys.argv[1])

    if num_players < 2:
        print("num_players must be at least 2")
        sys.exit(1)

    exe = Path("C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.exe")

    # Host
    subprocess.Popen([
        str(exe),
        "-fastmp",
        "host_standard",
    ])

    # Clients
    for i in range(num_players - 1):
        client_id = 1000 + i
        subprocess.Popen([
            str(exe),
            "-fastmp",
            "join",
            "-clientId",
            str(client_id),
        ])


if __name__ == "__main__":
    main()