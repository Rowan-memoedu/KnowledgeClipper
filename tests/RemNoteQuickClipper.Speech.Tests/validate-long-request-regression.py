"""Validate exact receipt paths through Win32 extended paths; never silently skip a long file."""
import argparse
import hashlib
import json
from pathlib import Path
from importlib.util import spec_from_file_location, module_from_spec


def native(path):
    text = str(path)
    return Path(text if text.startswith('\\\\?\\') else '\\\\?\\' + text)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--project-root', required=True)
    parser.add_argument('--receipt', required=True)
    args = parser.parse_args()
    spec = spec_from_file_location('public_validation', Path(__file__).with_name('validate-public-contracts.py'))
    public = module_from_spec(spec)
    spec.loader.exec_module(public)
    root = Path(args.project_root) / 'contracts/v1/schemas'
    registry, schemas = public.build_registry(root)
    validators = {name: public.load_validator(root / rel, registry, schemas) for name, rel in {
        'request': 'common/worker-request.schema.json', 'speech': 'speech/speech-request.schema.json',
        'result': 'speech/speech-result.schema.json', 'artifact': 'common/artifact.schema.json'
    }.items()}
    receipt = json.loads(native(args.receipt).read_text(encoding='utf-8'))
    checked = []

    def validate(path, kind):
        data = native(path).read_bytes()
        item = json.loads(data)
        validators[kind].validate(item)
        checked.append({'path': str(path), 'sha256': hashlib.sha256(data).hexdigest(), 'schema': kind})
        return item

    for case in receipt['cases']:
        for request_key, result_key in [('request', 'synthResult'), ('playbackRequest', 'playbackResult')]:
            request = validate(case[request_key], 'request')
            if request_key == 'request':
                validate(case[request_key], 'speech')
            result = validate(case[result_key], 'result')
            for key in ['requestId', 'operationId', 'taskId', 'runId', 'stageId', 'attemptId']:
                assert request[key] == result[key], key
            assert result['status'] == 'completed'
            job = Path(case[request_key])
            for _ in range(7):
                job = job.parent
            for output in result['outputs']:
                path = job / output['manifestPath']
                artifact = validate(path, 'artifact')
                assert hashlib.sha256(native(path).read_bytes()).hexdigest() == output['expectedSha256']
                for file in artifact['files']:
                    payload = native(job / file['path']).read_bytes()
                    assert len(payload) == file['bytes']
                    assert hashlib.sha256(payload).hexdigest() == file['sha256']
    output = Path(args.receipt).with_name('long-request-contract-receipt.json')
    with output.open('x', encoding='utf-8') as stream:
        json.dump({'status': 'passed', 'checked': checked, 'networkCalls': 0, 'speakerInvocations': 0}, stream, indent=2)
    print(f'PASS exact-path public contracts: {len(checked)} documents; receipt={output}')


if __name__ == '__main__':
    main()
