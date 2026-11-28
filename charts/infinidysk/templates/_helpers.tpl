{{- define "infinidysk.name" -}}
{{- if not (regexMatch "^[a-z]([-a-z0-9]*[a-z0-9])?$" .Release.Name) -}}
{{- fail "release name must start with a lowercase letter and contain only lowercase letters, digits and hyphens, ending in a letter or digit" -}}
{{- end -}}
{{- .Release.Name -}}
{{- end -}}

{{- define "infinidysk.selector" -}}
app.kubernetes.io/name: infinidysk
app.kubernetes.io/instance: {{ .Release.Name | quote }}
{{- end -}}

{{- define "infinidysk.value" -}}
{{- if kindIs "string" . -}}
{{- . -}}
{{- else -}}
{{- mustToJson . -}}
{{- end -}}
{{- end -}}

{{- define "infinidysk.check" -}}
{{- $values := .Values -}}
{{- if and $values.fuse.enabled (empty $values.fuse.hostMountPath) -}}
{{- fail "fuse.enabled requires fuse.hostMountPath for a shared mount" -}}
{{- end -}}
{{- if $values.fuse.hostMountPath -}}
{{- if not $values.fuse.enabled -}}
{{- fail "fuse.hostMountPath requires fuse.enabled=true" -}}
{{- end -}}
{{- if or (not (isAbs $values.fuse.hostMountPath)) (eq $values.fuse.hostMountPath "/") (ne (clean $values.fuse.hostMountPath) $values.fuse.hostMountPath) -}}
{{- fail "fuse.hostMountPath must be an absolute, normalized directory other than /" -}}
{{- end -}}
{{- if not (index $values.nodeSelector "kubernetes.io/hostname") -}}
{{- fail "fuse.hostMountPath requires nodeSelector[kubernetes.io/hostname]" -}}
{{- end -}}
{{- end -}}
{{- if and $values.ingress.enabled (empty $values.ingress.host) -}}
{{- fail "ingress.host is required when ingress is enabled" -}}
{{- end -}}
{{- if $values.httpRoute.enabled -}}
{{- if or (empty $values.httpRoute.parentRefs) (empty $values.httpRoute.hostnames) -}}
{{- fail "httpRoute needs parentRefs and hostnames" -}}
{{- end -}}
{{- end -}}
{{- if $values.serviceMonitor.enabled -}}
{{- if empty $values.adminApiKey.existingSecret -}}
{{- fail "serviceMonitor requires adminApiKey.existingSecret" -}}
{{- end -}}
{{- end -}}
{{- if and (not $values.fuse.enabled) (or (eq (int64 $values.puid) 0) (eq (int64 $values.pgid) 0)) -}}
{{- fail "WebDAV-only mode requires nonzero puid and pgid" -}}
{{- end -}}
{{- $owned := list "mount.fuse" -}}
{{- if $values.fuse.enabled -}}
{{- $owned = append $owned "mount.dir" -}}
{{- end -}}
{{- if $values.serviceMonitor.enabled -}}
{{- $owned = append $owned "metrics.prometheus" -}}
{{- end -}}
{{- if $values.adminApiKey.existingSecret -}}
{{- $owned = append $owned "auth.api_key" -}}
{{- end -}}
{{- $paths := concat (keys $values.settings) (keys $values.secretSettings) -}}
{{- range $path := $paths -}}
{{- range $reserved := $owned -}}
{{- if or (eq $path $reserved) (hasPrefix (printf "%s." $path) $reserved) (hasPrefix (printf "%s." $reserved) $path) -}}
{{- fail (printf "setting %s is controlled by a chart value" $path) -}}
{{- end -}}
{{- end -}}
{{- end -}}
{{- $seen := list -}}
{{- range $path := $paths -}}
{{- range $prior := $seen -}}
{{- if or (eq $path $prior) (hasPrefix (printf "%s." $path) $prior) (hasPrefix (printf "%s." $prior) $path) -}}
{{- fail "settings and secretSettings must not contain duplicate or overlapping setting paths" -}}
{{- end -}}
{{- end -}}
{{- $seen = append $seen $path -}}
{{- end -}}
{{- $secrets := list "providers" "arr_instances" "auth.api_key" "auth.session_key" "auth.oidc.client_secret" "sab.api_key" "webdav.password" "webdav.download_key" "imports.strm_key" "rclone_rc.password" -}}
{{- range $path, $value := $values.settings -}}
{{- range $secret := $secrets -}}
{{- if or (eq $path $secret) (hasPrefix (printf "%s." $path) $secret) (hasPrefix (printf "%s." $secret) $path) -}}
{{- fail (printf "setting %s belongs in secretSettings or an existing Secret" $path) -}}
{{- end -}}
{{- end -}}
{{- end -}}
{{- end -}}
