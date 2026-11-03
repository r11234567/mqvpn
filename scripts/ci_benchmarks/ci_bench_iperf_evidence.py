# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""Unabridged traffic evidence: no head/tail limits or silent command failures."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import platform
import shutil
import subprocess
import time


def stamp():
    return dict(utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                monotonic_ns=time.monotonic_ns())


def snapshot(prefix, phase, client, server, target):
    output = Path(str(prefix) + f'.network-{phase}.log')
    records = []
    commands = [['ip','-s','-d','address','show'], ['ip','route','show','table','all'],
                ['ip','rule','show'], ['ip','route','get',target], ['ss','-tinape'],
                ['ss','-uanpe'], ['tc','-s','-d','qdisc','show'], ['nstat','-az'],
                ['cat','/proc/net/snmp','/proc/net/netstat'], ['iptables-save','-c']]
    with output.open('wb') as log:
        for role, ns in [('client',client),('server',server)]:
            for command in commands:
                argv = ['ip','netns','exec',ns] + command
                record = dict(role=role, namespace=ns, argv=argv, **stamp())
                log.write((json.dumps(record)+'\n').encode()); log.flush()
                try:
                    result = subprocess.run(argv,stdout=log,stderr=subprocess.STDOUT,timeout=3)
                    record['exit_code'] = result.returncode
                except (OSError,subprocess.TimeoutExpired) as exc:
                    record['error'] = str(exc)
                log.write(('\n'+json.dumps(record)+'\n\n').encode())
                records.append(record)
    Path(str(prefix)+f'.network-{phase}.commands.json').write_text(json.dumps(records,indent=2)+'\n')


def finalize(prefix, archive, vpn_client='', vpn_server=''):
    sample = json.loads(prefix.read_text(encoding='utf-8'))
    meta = sample['_mqvpn']
    manifest = Path(str(prefix)+'.evidence.json')
    copy_errors = []
    if not meta['measurement_complete']:
        for role, source in [('client',vpn_client),('server',vpn_server)]:
            if source:
                try:
                    shutil.copyfile(source,str(prefix)+f'.vpn-{role}.full.log')
                except OSError as exc:
                    copy_errors.append(dict(role=role,source=source,error=str(exc)))
    meta['evidence_manifest'] = manifest.name
    prefix.write_text(json.dumps(sample,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    files = []
    for path in sorted(prefix.parent.glob(prefix.name+'*')):
        if path.is_file() and path != manifest:
            digest = hashlib.sha256()
            with path.open('rb') as stream:
                for chunk in iter(lambda: stream.read(1024*1024),b''): digest.update(chunk)
            files.append(dict(name=path.name,bytes=path.stat().st_size,sha256=digest.hexdigest()))
    manifest.write_text(json.dumps(dict(**stamp(),measurement_complete=meta['measurement_complete'],
        failure_reason=meta.get('failure_reason'),client_exit_code=meta.get('exit_code'),
        server_exit_code=meta.get('server_exit_code'),server_teardown=meta.get('server_teardown'),
        no_log_clipping=True,packet_scope='UDP tests: complete TCP control-port and ICMP packets only; not bulk UDP',
        files=files,copy_errors=copy_errors),indent=2)+'\n')
    archive.mkdir(parents=True,exist_ok=True)
    for path in prefix.parent.glob(prefix.name+'*'):
        if path.is_file() and path.resolve() != (archive/path.name).resolve():
            shutil.copy2(path,archive/path.name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command',choices=('context','snapshot','finalize'))
    parser.add_argument('prefix',type=Path)
    parser.add_argument('--phase',default='before')
    parser.add_argument('--client',default='')
    parser.add_argument('--server',default='')
    parser.add_argument('--target',default='')
    parser.add_argument('--client-args',default='')
    parser.add_argument('--proto',default='')
    parser.add_argument('--direction',default='')
    parser.add_argument('--scenario',default='unknown')
    parser.add_argument('--archive',type=Path)
    parser.add_argument('--vpn-client',default='')
    parser.add_argument('--vpn-server',default='')
    args = parser.parse_args()
    if args.command == 'context':
        doc = dict(**stamp(),client_namespace=args.client,server_namespace=args.server,target=args.target,
                   protocol=args.proto,direction=args.direction,scenario=args.scenario,
                   client_args=args.client_args,kernel=platform.release())
        Path(str(args.prefix)+'.context.json').write_text(json.dumps(doc,indent=2)+'\n')
    elif args.command == 'snapshot':
        snapshot(args.prefix,args.phase,args.client,args.server,args.target)
    else:
        if args.archive is None: parser.error('finalize requires --archive')
        finalize(args.prefix,args.archive,args.vpn_client,args.vpn_server)


if __name__ == '__main__':
    main()
