#!/usr/bin/env bash
# HambotV2 critical-CVE autofix (FR-214, design §5.4).
#
# Invoked by Jenkinsfile.security-fix after the scan job's artifacts were copied
# into the workspace. It reads the Trivy JSON reports archived by the last build
# of the security scan job and, for every CRITICAL finding in the NuGet SBOM
# reports (hambotv2 + bottests) that has a published FixedVersion, bumps the
# matching direct PackageReference in HambotV2/HambotV2.csproj and/or
# BotTests/BotTests.csproj to the highest fixed version within the installed
# package's own major. A cross-major fix is a breaking-change decision and is
# left for a human, as are transitive-only packages (no direct reference to
# bump), findings without a published fix, and image/apt findings (those clear
# by themselves via the scan's `docker build --pull` and the Dockerfile's
# `apt-get upgrade` on the next build).
#
# The script only edits the two .csproj files; it never runs dotnet, docker, or
# git — the pipeline verifies (build + test) before anything is pushed.
#
# Usage: autofix-criticals.sh <scan-security-dir> <repo-root> <out-dir>
#   <scan-security-dir>  directory holding cve-report.hambotv2.json,
#                        cve-report.bottests.json, cve-report.image.json
#   <repo-root>          repo checkout to patch (default: two levels above this
#                        script)
#   <out-dir>            receives plan.json (machine-readable), report.md and
#                        pr-body.md (PR body), plus the raw findings TSVs
#
# Exit codes: 0 = reports read (see plan.json "changed" for whether anything
# was patched), 2 = scan reports missing/unreadable (broken upstream scan).

set -euo pipefail

scan_dir="$1"
repo_root="${2:-$(cd "$(dirname "$0")/../.." && pwd)}"
out_dir="$3"

mkdir -p "$out_dir"

csprojs=("$repo_root/HambotV2/HambotV2.csproj" "$repo_root/BotTests/BotTests.csproj")
for f in "${csprojs[@]}"; do
    [ -f "$f" ] || { echo "autofix: missing project file $f" >&2; exit 2; }
done

missing=0
for r in hambotv2 bottests image; do
    f="$scan_dir/cve-report.$r.json"
    if [ ! -f "$f" ]; then
        echo "autofix: missing scan report $f" >&2
        missing=1
        continue
    fi
    # Fields are joined with \x1f (unit separator), not a tab: tab is IFS
    # whitespace, so `read` would collapse empty fields (an empty FixedVersion
    # would swallow the following field).
    jq -r '.Results[]?.Vulnerabilities[]?
           | select(.Severity == "CRITICAL")
           | [.VulnerabilityID, .PkgName, .InstalledVersion, (.FixedVersion // ""), (.Title // "")]
           | join("\u001f")' "$f" > "$out_dir/criticals.$r.tsv" || { echo "autofix: unreadable report $f" >&2; exit 2; }
done
[ "$missing" -eq 0 ] || exit 2

# Aggregate CRITICAL findings per package across the two NuGet reports:
# installed version (first seen) + all published fixed versions.
declare -A pkg_installed=() pkg_fixed=()
while IFS=$'\x1f' read -r _cve pkg inst fixed _title; do
    [ -n "$pkg" ] || continue
    pkg_installed["$pkg"]="${pkg_installed["$pkg"]:-$inst}"
    if [ -n "$fixed" ]; then
        for v in ${fixed//,/ }; do
            case " ${pkg_fixed["$pkg"]:-} " in *" $v "*) ;; *) pkg_fixed["$pkg"]="${pkg_fixed["$pkg"]:-} $v" ;; esac
        done
    fi
done < <(cat "$out_dir/criticals.hambotv2.tsv" "$out_dir/criticals.bottests.tsv")

# Highest fixed version within the installed major, but only if it is actually
# newer than the installed one; empty = not auto-fixable.
pick_target() {
    local pkg="$1" inst="$2" major v best=""
    major="${inst%%.*}"
    for v in ${pkg_fixed["$pkg"]:-}; do
        [ "${v%%.*}" = "$major" ] || continue
        if [ -z "$best" ] || [ "$(printf '%s\n%s\n' "$best" "$v" | sort -V | tail -1)" = "$v" ]; then
            best="$v"
        fi
    done
    if [ -n "$best" ] && [ "$best" != "$inst" ] \
        && [ "$(printf '%s\n%s\n' "$inst" "$best" | sort -V | tail -1)" = "$best" ]; then
        printf '%s' "$best"
    fi
}

# Current Version attribute of the PackageReference line for pkg in file, or "".
current_version() {
    awk -v pkg="$1" '
        tolower($0) ~ tolower("packagereference include=\"" pkg "\"") && match($0, /Version="[^"]*"/) {
            print substr($0, RSTART + 9, RLENGTH - 10); exit
        }' "$2"
}

# Rewrite the Version attribute of every PackageReference line for pkg.
bump_version() {
    local pkg="$1" ver="$2" file="$3" tmp
    tmp="$(mktemp)"
    awk -v pkg="$1" -v ver="$2" '
        tolower($0) ~ tolower("packagereference include=\"" pkg "\"") {
            sub(/Version="[^"]*"/, "Version=\"" ver "\"")
        }
        { print }' "$file" > "$tmp" && mv "$tmp" "$file"
}

bumps_tsv="$out_dir/bumps.tsv"; : > "$bumps_tsv"
manual_tsv="$out_dir/manual.tsv"; : > "$manual_tsv"

for pkg in "${!pkg_installed[@]}"; do
    inst="${pkg_installed["$pkg"]}"
    target="$(pick_target "$pkg" "$inst")"
    bumped_any=0
    found_ref=0
    for f in "${csprojs[@]}"; do
        if grep -qi "PackageReference Include=\"$pkg\"" "$f"; then
            found_ref=1
            cur="$(current_version "$pkg" "$f")"
            if [ -n "$target" ] && [ "$cur" != "$target" ]; then
                bump_version "$pkg" "$target" "$f"
                printf '%s\t%s\t%s\t%s\n' "${f#"$repo_root"/}" "$pkg" "${cur:-unknown}" "$target" >> "$bumps_tsv"
                bumped_any=1
            fi
        fi
    done
    if [ "$bumped_any" -eq 0 ]; then
        fixed_list="${pkg_fixed["$pkg"]:-none}"; fixed_list="${fixed_list# }"
        if [ "$found_ref" -eq 1 ]; then
            reason="already at the fixed version — no change needed"
        elif [ -z "$target" ]; then
            if [ -z "${pkg_fixed["$pkg"]:-}" ]; then
                reason="no published fix yet (awaiting upstream)"
            else
                reason="fix requires a major bump (installed $inst, fixed: $fixed_list) — manual review"
            fi
        else
            reason="transitive dependency — no direct PackageReference to bump; fixed via a parent package or a manual direct pin"
        fi
        printf '%s\t%s\t%s\t%s\n' "$pkg" "$inst" "$fixed_list" "$reason" >> "$manual_tsv"
    fi
done

# One row per CRITICAL finding (all three reports) for the PR body.
findings_tsv="$out_dir/findings.tsv"; : > "$findings_tsv"
for r in hambotv2 bottests image; do
    while IFS=$'\x1f' read -r cve pkg inst fixed _title; do
        [ -n "$cve" ] || continue
        if [ "$r" = "image" ]; then
            action="informational — image/apt layer; clears via the scan's docker build --pull and the Dockerfile apt-get upgrade (pinned apt majors need a manual bump)"
        elif awk -F'\t' -v p="$pkg" '$1==p{found=1} END{exit !found}' "$manual_tsv"; then
            action="manual — $(awk -F'\t' -v p="$pkg" '$1==p{print $4}' "$manual_tsv")"
        else
            action="auto-bumped (see table above)"
        fi
        printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$r" "$cve" "$pkg" "$inst" "${fixed:-none}" "$action" >> "$findings_tsv"
    done < "$out_dir/criticals.$r.tsv"
done

bumps_json=$(jq -R -s 'split("\n") | map(select(length > 0) | split("\t"))
    | map({file: .[0], package: .[1], from: .[2], to: .[3]})' "$bumps_tsv")
manual_json=$(jq -R -s 'split("\n") | map(select(length > 0) | split("\t"))
    | map({package: .[0], installed: .[1], fixed: .[2], reason: .[3]})' "$manual_tsv")
jq -n --argjson bumps "$bumps_json" --argjson manual "$manual_json" \
    '{changed: ($bumps | length > 0), bumps: $bumps, manual: $manual}' > "$out_dir/plan.json"

{
    echo "# CRITICAL findings from the weekly security scan"
    echo
    if [ -s "$bumps_tsv" ]; then
        echo "## Auto-applied fixes (direct PackageReference bumps)"
        echo
        echo "| Project file | Package | From | To |"
        echo "|---|---|---|---|"
        awk -F'\t' '{printf "| `%s` | %s | %s | %s |\n", $1, $2, $3, $4}' "$bumps_tsv"
        echo
    else
        echo "No CRITICAL finding could be auto-fixed (see below)."
        echo
    fi
    if [ -s "$manual_tsv" ]; then
        echo "## Needs a human"
        echo
        echo "| Package | Installed | Fixed versions | Why not automatic |"
        echo "|---|---|---|---|"
        awk -F'\t' '{printf "| %s | %s | %s | %s |\n", $1, $2, $3, $4}' "$manual_tsv"
        echo
    fi
    echo "## All CRITICAL findings"
    echo
    echo "| Report | CVE | Package | Installed | Fixed | Action |"
    echo "|---|---|---|---|---|---|"
    awk -F'\t' '{printf "| %s | %s | %s | %s | %s | %s |\n", $1, $2, $3, $4, $5, $6}' "$findings_tsv"
} > "$out_dir/report.md"

{
    echo "## Automated fix for CRITICAL security findings"
    echo
    [ -n "${SECURITY_JOB:-}" ] && echo "- Source scan: Jenkins job \`$SECURITY_JOB\` (last build with archived artifacts)"
    [ -n "${BUILD_URL:-}" ] && echo "- Autofix pipeline build: $BUILD_URL"
    echo "- This PR was generated automatically by \`Jenkinsfile.security-fix\`; the pipeline ran \`dotnet build\` + \`dotnet test\` on this branch before pushing. Review and merge manually — the pipeline never merges."
    echo
    cat "$out_dir/report.md"
} > "$out_dir/pr-body.md"

changed="$(jq -r '.changed' "$out_dir/plan.json")"
echo "autofix: changed=$changed bumps=$(wc -l < "$bumps_tsv") manual=$(wc -l < "$manual_tsv")"
exit 0
