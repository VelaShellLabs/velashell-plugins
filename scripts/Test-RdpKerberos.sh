#!/usr/bin/env bash
# Requires MIT krb5-kdc, krb5-admin-server, Python 3 and .NET. No system realm configuration is changed.
set -euo pipefail
for command in krb5kdc kdb5_util kadmin.local python3 openssl; do
    command -v "$command" >/dev/null || { echo "Missing Kerberos test prerequisite: $command" >&2; exit 1; }
done
work=$(mktemp -d)
kdc_pid=
cleanup() {
    if [[ -n "$kdc_pid" ]]; then
        kill "$kdc_pid" 2>/dev/null || true
        wait "$kdc_pid" 2>/dev/null || true
    fi
    rm -rf "$work"
}
trap cleanup EXIT
export KRB5_CONFIG="$work/krb5.conf"
export KRB5_KDC_PROFILE="$work/kdc.conf"
export KRB5_KTNAME="FILE:$work/service.keytab"
export KRB5CCNAME="FILE:$work/ccache"
export VELA_RDP_TEST_KRB5_USER='vela@VELA.TEST'
export VELA_RDP_TEST_KRB5_HOST='rdp.vela.test'
VELA_RDP_TEST_KRB5_PASSWORD=$(openssl rand -hex 24)
export VELA_RDP_TEST_KRB5_PASSWORD
port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()')
cat > "$KRB5_CONFIG" <<EOF
[libdefaults]
 default_realm = VELA.TEST
 dns_lookup_kdc = false
 dns_lookup_realm = false
 dns_canonicalize_hostname = false
 rdns = false
[realms]
 VELA.TEST = {
  kdc = 127.0.0.1:$port
 }
[domain_realm]
 .vela.test = VELA.TEST
 vela.test = VELA.TEST
EOF
cat > "$KRB5_KDC_PROFILE" <<EOF
[kdcdefaults]
 kdc_listen = 127.0.0.1:$port
 kdc_tcp_listen = 127.0.0.1:$port
[realms]
 VELA.TEST = {
  database_name = $work/principal
  key_stash_file = $work/stash
  acl_file = $work/kadm5.acl
 }
EOF
# Allows locally extracted Debian tools as well as installed MIT Kerberos.
if [[ -n "${VELA_RDP_TEST_KDB_MODULE_DIR:-}" ]]; then
    printf '\n[dbmodules]\n db_module_dir = %s\n' "$VELA_RDP_TEST_KDB_MODULE_DIR" >> "$KRB5_KDC_PROFILE"
fi
kdb5_util create -s -P "$(openssl rand -hex 24)" > "$work/setup.log" 2>&1
kadmin.local -q "addprinc -pw $VELA_RDP_TEST_KRB5_PASSWORD $VELA_RDP_TEST_KRB5_USER" >> "$work/setup.log" 2>&1
kadmin.local -q "addprinc -randkey TERMSRV/$VELA_RDP_TEST_KRB5_HOST" >> "$work/setup.log" 2>&1
kadmin.local -q "ktadd -k $work/service.keytab TERMSRV/$VELA_RDP_TEST_KRB5_HOST" >> "$work/setup.log" 2>&1
krb5kdc -n -P "$work/kdc.pid" > "$work/kdc.log" 2>&1 &
kdc_pid=$!
python3 - "$port" <<'PY'
import socket, sys, time
for attempt in range(50):
    try:
        with socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=0.1):
            break
    except OSError:
        time.sleep(0.1)
else:
    sys.exit("Temporary Kerberos KDC did not become ready")
PY
if [[ $# -gt 0 ]]; then
    "$@"
else
    dotnet test tests/VelaShell.Plugin.Rdp.Tests/VelaShell.Plugin.Rdp.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~KerberosNlaTests'
fi
