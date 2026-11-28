import base64
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

import yaml


ROOT = Path(__file__).resolve().parents[2]
CHART = ROOT / "charts" / "infinidysk"
BINARY = os.environ.get("HELM_TEST_BINARY")


class ChartTests(unittest.TestCase):
    def render(self, values=None, chart=CHART, error=None, release_name="fixture"):
        with tempfile.TemporaryDirectory(prefix="infinidysk-helm-") as directory:
            value_file = Path(directory) / "values.json"
            value_file.write_text(json.dumps(values or {}), encoding="utf-8")
            command = [
                "helm", "template", release_name, str(chart),
                "--namespace", "fixture", "--kube-version", "1.31.0",
                "--values", str(value_file),
            ]
            result = subprocess.run(
                command, cwd=ROOT, text=True, capture_output=True, check=False
            )
        if error is not None:
            self.assertNotEqual(result.returncode, 0, "invalid values rendered")
            self.assertIn(error, result.stderr)
            return []
        self.assertEqual(result.returncode, 0, "helm template failed for a valid fixture")
        return [document for document in yaml.safe_load_all(result.stdout) if document]

    def resource(self, documents, kind):
        matches = [document for document in documents if document["kind"] == kind]
        self.assertEqual(len(matches), 1, f"expected one {kind}")
        return matches[0]

    def container(self, documents):
        deployment = self.resource(documents, "Deployment")
        containers = deployment["spec"]["template"]["spec"]["containers"]
        self.assertEqual(len(containers), 1)
        return containers[0]

    def environment(self, documents, external_secrets):
        secrets = dict(external_secrets)
        for document in documents:
            if document["kind"] == "Secret":
                secrets[document["metadata"]["name"]] = {
                    name: base64.b64decode(value, validate=True).decode("utf-8")
                    for name, value in document["data"].items()
                }
        container = self.container(documents)
        environment = {}
        for source in container.get("envFrom", []):
            environment.update(secrets[source["secretRef"]["name"]])
        for variable in container["env"]:
            if "value" in variable:
                environment[variable["name"]] = variable["value"].replace("$$", "$")
            else:
                reference = variable["valueFrom"]["secretKeyRef"]
                environment[variable["name"]] = secrets[reference["name"]][reference["key"]]
        self.assertTrue(all(isinstance(value, str) for value in environment.values()))
        return environment

    def test_setting_types_and_secret_placement(self):
        provider_password = "fixture-provider-$(PUID)-$$"
        values = {
            "settings": {
                "queue.concurrency": 3,
                "queue.extra_blocklist": ["fixture-$(PUID)-$$"],
                "health.enabled": False,
                "maintenance.window": {"start": "03:00", "end": "06:00"},
                "imports.library_dirs": [{"path": "/library", "arr_path": "/library"}],
            },
            "secretSettings": {
                "webdav.password": "0123456789",
                "providers": [{
                    "name": "fixture-provider", "hostname": "example.invalid",
                    "port": 563, "username": "fixture-user",
                    "password": provider_password, "enabled": False,
                }],
            },
        }
        documents = self.render(values)
        literal_env = {
            variable["name"]: variable["value"]
            for variable in self.container(documents)["env"] if "value" in variable
        }
        self.assertEqual(
            literal_env["INFINIDYSK__QUEUE__EXTRA_BLOCKLIST"],
            '["fixture-$$(PUID)-$$$$"]',
        )
        secret = self.resource(documents, "Secret")
        provider_json = base64.b64decode(
            secret["data"]["INFINIDYSK__PROVIDERS"], validate=True
        ).decode("utf-8")
        self.assertEqual(json.loads(provider_json)[0]["password"], provider_password)
        environment = self.environment(documents, {})
        self.assertEqual(environment["INFINIDYSK__WEBDAV__PASSWORD"], "0123456789")
        self.assertEqual(environment["INFINIDYSK__QUEUE__CONCURRENCY"], "3")
        self.assertEqual(environment["INFINIDYSK__HEALTH__ENABLED"], "false")
        for path, name in (
            ("queue.extra_blocklist", "INFINIDYSK__QUEUE__EXTRA_BLOCKLIST"),
            ("maintenance.window", "INFINIDYSK__MAINTENANCE__WINDOW"),
            ("imports.library_dirs", "INFINIDYSK__IMPORTS__LIBRARY_DIRS"),
        ):
            self.assertEqual(json.loads(environment[name]), values["settings"][path])
        self.assertEqual(
            json.loads(environment["INFINIDYSK__PROVIDERS"]),
            values["secretSettings"]["providers"],
        )
        public_documents = json.dumps([
            document for document in documents if document["kind"] != "Secret"
        ])
        for marker in ("0123456789", provider_password):
            self.assertNotIn(marker, public_documents)
        template = self.resource(documents, "Deployment")["spec"]["template"]
        original_checksum = template["metadata"]["annotations"]["checksum/settings"]
        values["secretSettings"]["webdav.password"] = "fixture-new-password"
        changed = self.resource(self.render(values), "Deployment")["spec"]["template"]
        self.assertNotEqual(
            original_checksum, changed["metadata"]["annotations"]["checksum/settings"]
        )

    @unittest.skipUnless(BINARY, "set HELM_TEST_BINARY for application-contract validation")
    def test_rendered_environment_passes_application_preflight(self):
        values = {
            "puid": 1000680000,
            "pgid": 1000680001,
            "settings": {
                "health.enabled": False,
                "queue.concurrency": 3,
                "queue.extra_blocklist": ["fixture-$(PUID)-$$"],
            },
            "secretSettings": {"webdav.password": "0123456789"},
            "existingEnvSecret": "fixture-environment",
            "database": {"existingSecret": "fixture-database"},
            "adminApiKey": {"existingSecret": "fixture-api"},
            "serviceMonitor": {"enabled": True},
        }
        external = {
            "fixture-environment": {
                "INFINIDYSK_ADMIN_USERNAME": "fixture-admin",
                "INFINIDYSK_ADMIN_PASSWORD": "fixture-admin-password",
                "INFINIDYSK__PROVIDERS": json.dumps([{
                    "name": "fixture-provider", "hostname": "example.invalid",
                    "port": 563, "username": "fixture-user",
                    "password": "fixture-provider-password", "enabled": False,
                }]),
            },
            "fixture-database": {"url": "postgres://fixture:fixture@127.0.0.1:1/fixture"},
            "fixture-api": {"api-key": "fixture-admin-api-key"},
        }
        documents = self.render(values)
        pod = self.resource(documents, "Deployment")["spec"]["template"]["spec"]
        security = self.container(documents)["securityContext"]
        for actual, expected in (
            (security["runAsUser"], values["puid"]),
            (security["runAsGroup"], values["pgid"]),
            (pod["securityContext"]["fsGroup"], values["pgid"]),
        ):
            self.assertIs(type(actual), int)
            self.assertEqual(actual, expected)
        environment = self.environment(documents, external)
        self.assertEqual(environment["PUID"], "1000680000")
        self.assertEqual(environment["PGID"], "1000680001")
        self.assertEqual(environment["INFINIDYSK_ADMIN_USERNAME"], "fixture-admin")
        self.assertEqual(environment["INFINIDYSK_ADMIN_PASSWORD"], "fixture-admin-password")
        environment["PATH"] = os.environ.get("PATH", os.defpath)
        result = subprocess.run(
            [str(Path(BINARY).resolve()), "config", "validate"],
            env=environment, cwd=ROOT, text=True, capture_output=True, check=False,
            timeout=30,
        )
        self.assertEqual(result.returncode, 0, "application rejected the rendered fixture")
        for marker in ("fixture-admin-password", "fixture-provider-password", "fixture-admin-api-key"):
            self.assertNotIn(marker, result.stdout + result.stderr)

    def test_packaged_chart_pins_the_matching_image(self):
        with tempfile.TemporaryDirectory(prefix="infinidysk-package-") as directory:
            result = subprocess.run(
                ["helm", "package", str(CHART), "--version", "2.0.0-beta.999",
                 "--app-version", "v2.0.0-beta.999", "--destination", directory],
                cwd=ROOT, text=True, capture_output=True, check=False,
            )
            self.assertEqual(result.returncode, 0, "helm package failed")
            documents = self.render(chart=Path(directory) / "infinidysk-2.0.0-beta.999.tgz")
        self.assertEqual(
            self.container(documents)["image"],
            "ghcr.io/infinidysk/infinidysk-next:v2.0.0-beta.999",
        )

    def test_default_webdav_is_singleton_and_persistent(self):
        documents = self.render(release_name="on")
        self.assertEqual(
            documents, self.render({"global": {"fixture": "ignored"}}, release_name="on")
        )
        self.assertEqual(
            sorted(document["kind"] for document in documents),
            ["Deployment", "PersistentVolumeClaim", "Service"],
        )
        deployment = self.resource(documents, "Deployment")
        service = self.resource(documents, "Service")
        claim = self.resource(documents, "PersistentVolumeClaim")
        self.assertEqual(deployment["metadata"]["name"], "on")
        self.assertEqual(service["metadata"]["name"], "on")
        self.assertEqual(claim["metadata"]["name"], "on-config")
        spec = deployment["spec"]
        self.assertEqual(spec["replicas"], 1)
        self.assertEqual(spec["strategy"], {"type": "Recreate"})
        selector = spec["selector"]["matchLabels"]
        self.assertEqual(selector, spec["template"]["metadata"]["labels"])
        self.assertEqual(selector, service["spec"]["selector"])
        pod = spec["template"]["spec"]
        self.assertEqual(
            pod["securityContext"],
            {"fsGroup": 1000, "fsGroupChangePolicy": "OnRootMismatch"},
        )
        self.assertEqual(pod["terminationGracePeriodSeconds"], 60)
        self.assertIs(pod["automountServiceAccountToken"], False)
        self.assertIs(pod["enableServiceLinks"], False)
        self.assertEqual(
            pod["volumes"],
            [{"name": "config", "persistentVolumeClaim": {"claimName": "on-config"}}],
        )
        container = self.container(documents)
        self.assertEqual(container["image"], "ghcr.io/infinidysk/infinidysk-next:nightly")
        self.assertEqual(container["securityContext"], {
            "runAsUser": 1000, "runAsGroup": 1000, "runAsNonRoot": True,
            "privileged": False, "allowPrivilegeEscalation": False,
            "capabilities": {"drop": ["ALL"]},
            "seccompProfile": {"type": "RuntimeDefault"},
        })
        self.assertEqual(
            container["volumeMounts"], [{"name": "config", "mountPath": "/config"}]
        )
        self.assertEqual(claim["spec"], {
            "accessModes": ["ReadWriteOnce"],
            "resources": {"requests": {"storage": "10G"}},
        })
        self.assertEqual(claim["metadata"]["annotations"], {"helm.sh/resource-policy": "keep"})
        environment = self.environment(documents, {})
        self.assertEqual(environment["INFINIDYSK__MOUNT__FUSE"], "false")
        self.assertEqual(environment["INFINIDYSK_CONFIG_DIR"], "/config")
        self.assertNotIn("INFINIDYSK__MOUNT__DIR", environment)
        self.assertNotIn("INFINIDYSK_LISTEN", environment)
        self.assertEqual(container["ports"], [{"name": "http", "containerPort": 3000}])
        self.assertEqual(
            container["readinessProbe"]["httpGet"], {"path": "/healthz", "port": "http"}
        )
        for absent in ("startupProbe", "livenessProbe", "lifecycle"):
            self.assertNotIn(absent, container)

    def test_fuse_opt_in_and_existing_storage(self):
        library_volume = {
            "name": "library", "persistentVolumeClaim": {"claimName": "fixture-library"}
        }
        library_mount = {"name": "library", "mountPath": "/library"}
        documents = self.render({
            "fuse": {"enabled": True, "hostMountPath": "/var/lib/infinidysk-fixture"},
            "nodeSelector": {"kubernetes.io/hostname": "fixture-node"},
            "persistence": {"existingClaim": "fixture-config-external"},
            "extraVolumes": [library_volume],
            "extraVolumeMounts": [library_mount],
        })
        self.assertNotIn("PersistentVolumeClaim", [document["kind"] for document in documents])
        container = self.container(documents)
        self.assertEqual(container["securityContext"], {
            "runAsUser": 0, "runAsGroup": 0, "runAsNonRoot": False,
            "privileged": True, "allowPrivilegeEscalation": True,
        })
        environment = self.environment(documents, {})
        self.assertEqual(environment["INFINIDYSK__MOUNT__FUSE"], "true")
        self.assertEqual(environment["INFINIDYSK__MOUNT__DIR"], "/mnt/infinidysk")
        pod = self.resource(documents, "Deployment")["spec"]["template"]["spec"]
        self.assertEqual(pod["volumes"], [
            {"name": "config",
             "persistentVolumeClaim": {"claimName": "fixture-config-external"}},
            {"name": "mount",
             "hostPath": {"path": "/var/lib/infinidysk-fixture", "type": "Directory"}},
            {"name": "fuse", "hostPath": {"path": "/dev/fuse", "type": "CharDevice"}},
            {"name": "bdi",
             "hostPath": {"path": "/sys/devices/virtual/bdi", "type": "Directory"}},
            library_volume,
        ])
        self.assertEqual(container["volumeMounts"], [
            {"name": "config", "mountPath": "/config"},
            {"name": "mount", "mountPath": "/mnt", "mountPropagation": "Bidirectional"},
            {"name": "fuse", "mountPath": "/dev/fuse"},
            {"name": "bdi", "mountPath": "/sys/devices/virtual/bdi"},
            library_mount,
        ])
        self.assertEqual(pod["nodeSelector"], {
            "kubernetes.io/os": "linux", "kubernetes.io/hostname": "fixture-node",
        })
        classless = self.render({"persistence": {"storageClass": ""}})
        self.assertEqual(
            self.resource(classless, "PersistentVolumeClaim")["spec"]["storageClassName"], ""
        )

    def test_external_mount_preserves_webdav_mode(self):
        volume = {
            "name": "mount",
            "hostPath": {"path": "/var/lib/fixture-external", "type": "Directory"},
        }
        mount = {"name": "mount", "mountPath": "/mnt", "mountPropagation": "HostToContainer"}
        documents = self.render({
            "settings": {"mount.dir": "/mnt/external"},
            "extraVolumes": [volume],
            "extraVolumeMounts": [mount],
        })
        container = self.container(documents)
        pod = self.resource(documents, "Deployment")["spec"]["template"]["spec"]
        self.assertEqual([item["name"] for item in pod["volumes"]], ["config", "mount"])
        self.assertIn(volume, pod["volumes"])
        self.assertEqual(
            container["volumeMounts"], [{"name": "config", "mountPath": "/config"}, mount]
        )
        self.assertEqual(
            [item["value"] for item in container["env"]
             if item["name"] == "INFINIDYSK__MOUNT__DIR"],
            ["/mnt/external"],
        )
        self.assertEqual(self.environment(documents, {})["INFINIDYSK__MOUNT__FUSE"], "false")
        self.assertIs(container["securityContext"]["runAsNonRoot"], True)
        self.assertIs(container["securityContext"]["privileged"], False)

    def test_optional_routes_and_metrics_use_correct_targets(self):
        host = "fixture.example.invalid"
        documents = self.render({
            "ingress": {
                "enabled": True, "host": host, "className": "fixture",
                "tlsSecret": "fixture-tls",
            },
            "httpRoute": {
                "enabled": True, "parentRefs": [{"name": "fixture-gateway"}],
                "hostnames": [host],
            },
            "serviceMonitor": {"enabled": True},
            "adminApiKey": {"existingSecret": "fixture-api", "key": "api-key"},
        })
        self.assertEqual(self.resource(documents, "Ingress")["spec"], {
            "ingressClassName": "fixture",
            "tls": [{"secretName": "fixture-tls", "hosts": [host]}],
            "rules": [{"host": host, "http": {"paths": [{
                "path": "/", "pathType": "Prefix",
                "backend": {"service": {"name": "fixture", "port": {"name": "http"}}},
            }]}}],
        })
        self.assertEqual(self.resource(documents, "HTTPRoute")["spec"], {
            "parentRefs": [{"name": "fixture-gateway"}],
            "hostnames": [host],
            "rules": [{
                "matches": [{"path": {"type": "PathPrefix", "value": "/"}}],
                "backendRefs": [{"name": "fixture", "port": 3000}],
            }],
        })
        monitor = self.resource(documents, "ServiceMonitor")["spec"]
        service = self.resource(documents, "Service")
        self.assertEqual(monitor["selector"]["matchLabels"], service["metadata"]["labels"])
        self.assertEqual(len(monitor["endpoints"]), 1)
        endpoint = monitor["endpoints"][0]
        self.assertEqual(endpoint["path"], "/metrics")
        self.assertEqual(endpoint["port"], "http")
        self.assertIn("http", [port["name"] for port in service["spec"]["ports"]])
        self.assertEqual(endpoint["authorization"]["type"], "Bearer")
        variables = {item["name"]: item for item in self.container(documents)["env"]}
        credentials = endpoint["authorization"]["credentials"]
        self.assertEqual(credentials, {"name": "fixture-api", "key": "api-key"})
        self.assertEqual(
            credentials, variables["INFINIDYSK__AUTH__API_KEY"]["valueFrom"]["secretKeyRef"]
        )
        self.assertEqual(variables["INFINIDYSK__METRICS__PROMETHEUS"]["value"], "true")

    def test_invalid_values_fail_before_install(self):
        shared = {"enabled": True, "hostMountPath": "/var/lib/infinidysk-fixture"}
        node = {"kubernetes.io/hostname": "fixture-node"}
        monitor = {
            "serviceMonitor": {"enabled": True},
            "adminApiKey": {"existingSecret": "fixture-api"},
        }
        cases = (
            ({"fuse": {"enabled": True}}, "fuse.enabled requires fuse.hostMountPath"),
            ({"fuse": {**shared, "enabled": False}},
             "hostMountPath requires fuse.enabled=true"),
            ({"fuse": shared}, "hostMountPath requires nodeSelector"),
            ({"serviceMonitor": {"enabled": True}},
             "serviceMonitor requires adminApiKey.existingSecret"),
            ({"settings": {"providers": []}}, "belongs in secretSettings"),
            # Helm's schema errors do not name the failing map reliably.
            ({"settings": {"auth__api_key": "fixture-key"}},
             "invalid propertyName 'auth__api_key'"),
            ({"settings": {"queue": {"concurrency": 2}},
              "secretSettings": {"queue.concurrency": 3}},
             "duplicate or overlapping setting paths"),
            ({"fuse": shared, "nodeSelector": node,
              "settings": {"mount.dir": "/mnt/other"}}, "controlled by a chart value"),
            ({**monitor, "secretSettings": {"auth.api_key": "fixture-key"}},
             "controlled by a chart value"),
        )
        with self.subTest(error="release name"):
            self.render(
                release_name="media.test",
                error="release name must start with a lowercase letter",
            )
        for values, error in cases:
            with self.subTest(error=error):
                self.render(values, error=error)


if __name__ == "__main__":
    if BINARY and not Path(BINARY).is_file():
        raise SystemExit("HELM_TEST_BINARY does not name the built application")
    unittest.main()
