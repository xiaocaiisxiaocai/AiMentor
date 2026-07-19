{{- define "aimentor.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "aimentor.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name (include "aimentor.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{- define "aimentor.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | quote }}
app.kubernetes.io/name: {{ include "aimentor.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "aimentor.selectorLabels" -}}
app.kubernetes.io/name: {{ include "aimentor.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/component: api
{{- end -}}

{{- define "aimentor.image" -}}
{{- $repository := required "image.repository 必须由发布流水线设置" .Values.image.repository -}}
{{- $digest := required "image.digest 必须由发布流水线设置" .Values.image.digest -}}
{{- if not (regexMatch "^sha256:[0-9a-f]{64}$" $digest) -}}
{{- fail "image.digest 必须是小写 sha256 OCI manifest digest" -}}
{{- end -}}
{{- $releaseId := required "releaseId 必须绑定镜像 digest" .Values.releaseId -}}
{{- if ne $releaseId $digest -}}
{{- fail "releaseId 必须与 image.digest 完全一致" -}}
{{- end -}}
{{- printf "%s@%s" $repository $digest -}}
{{- end -}}

{{- define "aimentor.existingSecret" -}}
{{- required "existingSecret 必须引用由外部密钥管理流程创建的 Secret" .Values.existingSecret -}}
{{- end -}}

{{- define "aimentor.migrationExistingSecret" -}}
{{- $secret := required "migration.existingSecret 必须引用只含工作流数据库连接串的最小权限 Secret" .Values.migration.existingSecret -}}
{{- if eq $secret .Values.existingSecret -}}
{{- fail "migration.existingSecret 禁止复用 API existingSecret" -}}
{{- end -}}
{{- $secret -}}
{{- end -}}

{{- define "aimentor.dataProtectionClaim" -}}
{{- required "dataProtection.existingClaim 必须引用共享密钥环 PVC" .Values.dataProtection.existingClaim -}}
{{- end -}}

{{- define "aimentor.dataProtectionCertificateSecret" -}}
{{- required "dataProtection.existingCertificateSecret 必须引用密钥环保护证书 Secret" .Values.dataProtection.existingCertificateSecret -}}
{{- end -}}
