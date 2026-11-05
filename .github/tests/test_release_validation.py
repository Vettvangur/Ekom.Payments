"""Run the workflow's actual PowerShell validation (requires pwsh on PATH)."""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import textwrap
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = (ROOT / '.github/workflows/publish-ekom-payments.yml').read_text()


def step_script(name):
    section = WORKFLOW.split('      - name: ' + name + '\n', 1)[1]
    section = section.split('\n      - ', 1)[0]
    return textwrap.dedent(section.split('        run: |\n', 1)[1])


class ReleaseValidationTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.groups = json.loads((ROOT / '.github/publish-projects.json').read_text())
        self.manifest = json.loads((ROOT / '.release-please-manifest.json').read_text())
        files = {'.github/publish-projects.json', '.release-please-manifest.json'}
        files.update(g['version-file'] for g in self.groups.values())
        for file in files:
            target = self.root / file
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / file, target)
        self.script = self.root / 'validate.ps1'
        self.script.write_text(step_script('Validate release version'))

    def validate(self, component, version, ref_type='tag'):
        env = dict(os.environ, RELEASE_TAG=f'{component}-v{version}',
                   RELEASE_REF_TYPE=ref_type, GITHUB_ENV=str(self.root / 'github-env'))
        return subprocess.run(['pwsh', '-NoProfile', '-File', str(self.script)],
                              cwd=self.root, env=env, capture_output=True, text=True)

    def test_all_component_versions(self):
        for component, group in self.groups.items():
            with self.subTest(component=component):
                result = self.validate(component, self.manifest[group['path']])
                self.assertEqual(result.returncode, 0, result.stderr)

    def test_multiple_property_groups_do_not_create_false_mismatch(self):
        component = 'Ekom.Payments.TeyaConsumerloans'
        group = self.groups[component]
        path = self.root / group['version-file']
        source = path.read_text().replace('</Project>', '<PropertyGroup><Other>value</Other></PropertyGroup></Project>')
        path.write_text(source)
        result = self.validate(component, self.manifest[group['path']])
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_real_build_version_mismatch_is_rejected(self):
        component = 'Ekom.Payments.TeyaConsumerloans'
        group = self.groups[component]
        path = self.root / group['version-file']
        path.write_text(re.sub(r'<Version>[^<]+</Version>', '<Version>0.2.999</Version>', path.read_text()))
        result = self.validate(component, self.manifest[group['path']])
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('build=0.2.999', result.stderr)

    def test_manifest_mismatch_is_rejected(self):
        self.manifest['.'] = '0.2.999'
        (self.root / '.release-please-manifest.json').write_text(json.dumps(self.manifest))
        result = self.validate('Ekom.Payments', '0.2.98')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('manifest=0.2.999', result.stderr)

    def test_provider_package_and_core_dependency_validation(self):
        component = 'Ekom.Payments.TeyaConsumerloans'
        version = self.manifest[self.groups[component]['path']]
        artifacts = self.root / 'artifacts'
        artifacts.mkdir()
        self.script.write_text(step_script('Verify package versions and internal dependencies'))
        (artifacts / f'{component}.{version}.snupkg').touch()
        for dependency, succeeds in [('0.2.97', True), ('0.2.999', False)]:
            with self.subTest(dependency=dependency):
                with zipfile.ZipFile(artifacts / f'{component}.{version}.nupkg', 'w') as package:
                    package.writestr(component + '.nuspec',
                        f'<package><metadata><id>{component}</id><version>{version}</version>'
                        f'<dependencies><group><dependency id="Ekom.Payments.Core" version="{dependency}"/>'
                        '</group></dependencies></metadata></package>')
                env = dict(os.environ, RELEASE_COMPONENT=component, RELEASE_VERSION=version)
                result = subprocess.run(['pwsh', '-NoProfile', '-File', str(self.script)],
                                        cwd=self.root, env=env, capture_output=True, text=True)
                self.assertEqual(result.returncode == 0, succeeds, result.stderr)

    def test_unknown_component_and_branch_are_rejected(self):
        self.assertNotEqual(self.validate('Ekom.Payments.Unknown', '0.2.98').returncode, 0)
        self.assertNotEqual(self.validate('Ekom.Payments', '0.2.98', 'branch').returncode, 0)


if __name__ == '__main__':
    unittest.main()
