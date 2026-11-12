#!/usr/bin/env python3
"""Exercise the launcher without starting containers or changing local secrets."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    (root / 'scripts').mkdir()
    shutil.copyfile(Path(__file__).with_name('demo-lan.sh'), root / 'scripts/demo-lan.sh')
    (root / 'bin').mkdir()
    for name, body in {
        'docker': 'printf "%s\\n" "$*|$API_BIND|$Session__AllowedOrigins__0" >> "$MOCK_LOG"',
        'ip': 'echo "1.1.1.1 via 192.168.1.1 dev wlan0 src 192.168.1.20 uid 1000"',
        'openssl': 'echo generated-test-secret; echo generated >> "$MOCK_KEYS"',
    }.items():
        executable = root / 'bin' / name
        executable.write_text('#!/bin/bash\n' + body + '\n')
        executable.chmod(0o755)
    env = {key: value for key, value in os.environ.items()
           if key not in ('JWT_SIGNING_KEY', 'LAN_IP', 'API_BIND', 'Session__AllowedOrigins__0')}
    env.update(PATH=f"{root / 'bin'}:{env['PATH']}", MOCK_LOG=str(root / 'calls'),
               MOCK_KEYS=str(root / 'keys'))

    def run(*args, **overrides):
        return subprocess.run(['bash', str(root / 'scripts/demo-lan.sh'), *args],
                              env=env | overrides, capture_output=True, text=True)

    first = run()
    assert first.returncode == 0, first.stderr
    assert 'http://192.168.1.20:8080/health' in first.stdout
    assert 'sudo firewall-cmd --add-port=8080/tcp' in first.stdout
    assert 'generated-test-secret' not in first.stdout + first.stderr
    assert (root / '.env.demo').stat().st_mode & 0o777 == 0o600
    assert 'up -d --build --wait|0.0.0.0|http://192.168.1.20:8080' in (root / 'calls').read_text()
    assert run(LAN_IP='10.0.0.5').returncode == 0
    assert (root / 'keys').read_text() == 'generated\n'
    count = len((root / 'calls').read_text().splitlines())
    for invalid in ('127.0.0.1', '999.1.2.3', 'not-an-ip', '0.0.0.0'):
        assert run(LAN_IP=invalid).returncode != 0
    assert run('unexpected').returncode != 0
    assert len((root / 'calls').read_text().splitlines()) == count
    assert run('down').returncode == 0
    assert ' down|' in (root / 'calls').read_text()
    (root / '.env.demo').unlink()
    assert run(JWT_SIGNING_KEY='provided-test-key').returncode == 0
    assert not (root / '.env.demo').exists()
    assert run('down').returncode == 0
    (root / 'bin/ip').write_text('#!/bin/bash\nexit 1\n')
    assert run().returncode != 0
    (root / 'bin/openssl').write_text('#!/bin/bash\nexit 1\n')
    assert run(LAN_IP='10.0.0.5').returncode != 0
    assert not (root / '.env.demo').exists()
print('PASS: detection, override, invalid/missing IP, key generation/reuse/override/failure, permissions, startup, shutdown, secret-free output')
