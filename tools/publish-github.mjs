#!/usr/bin/env node
/**
 * 把当前工作区发布到 GitHub（走 REST API，不依赖 git push——这台机器 443 到 github.com 的 git 通道被拦）。
 *
 * 用法：node tools/publish-github.mjs [--repo PwA] [--tag v1.1.0] [--message "..."] [--dry]
 *
 * 行为：
 *  1. 读 %USERPROFILE%\.pwa\github-token.txt；
 *  2. 仓库不存在就创建（公开）；
 *  3. 用 Git Data API 建 blobs/tree/commit，更新 main（有历史就挂父提交）；
 *  4. 创建 release（说明取 docs/发布说明-<tag 去掉 v>.md），上传 release/*.zip 作为资产；
 *  5. 打印仓库与 release 地址。
 *
 * 只发布 src/docs/tools/release 相关文件；backups、ref、tmp-* 不发布。
 */
import { execFileSync } from 'node:child_process'
import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { homedir } from 'node:os'

const args = process.argv.slice(2)
const arg = (name, fallback) => {
  const i = args.indexOf(name)
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback
}
const REPO = arg('--repo', 'PwA')
const TAG = arg('--tag', 'v1.0.0')
const BRANCH = arg('--branch', 'main')
const DRY = args.includes('--dry')
const ROOT = resolve(process.cwd())
/** 提交说明：`--message` 优先，否则按 tag 生成一句。 */
const MESSAGE = arg('--message', `PwA ${TAG}: docs, worlds and release packages`)
/** 发布说明：docs/发布说明-<tag>.md（tag 去掉开头的 v），回退到 1.0.0 那份。 */
function notesFile() {
  const version = TAG.replace(/^v/i, '')
  for (const name of [`发布说明-${version}.md`, '发布说明-1.0.0.md']) {
    const path = join(ROOT, 'docs', name)
    if (existsSync(path)) return path
  }
  return null
}

const tokenFile = join(homedir(), '.pwa', 'github-token.txt')
if (!existsSync(tokenFile)) {
  console.error(`找不到 token：${tokenFile}`)
  process.exit(1)
}
const TOKEN = readFileSync(tokenFile, 'utf8').trim()
const API = 'https://api.github.com'

async function api(path, options = {}) {
  const response = await fetch(`${API}${path}`, {
    ...options,
    headers: {
      Authorization: `Bearer ${TOKEN}`,
      Accept: 'application/vnd.github+json',
      'User-Agent': 'PwA-release',
      'X-GitHub-Api-Version': '2022-11-28',
      ...(options.headers ?? {}),
    },
  })
  const text = await response.text()
  let body = null
  try { body = text ? JSON.parse(text) : null } catch { body = text }
  if (!response.ok) {
    const message = body?.message ?? response.statusText
    const error = new Error(`${options.method ?? 'GET'} ${path} -> ${response.status}: ${message}`)
    error.status = response.status
    error.body = body
    throw error
  }
  return body
}

const GIT_BIN = process.env.GIT_BIN
  || (existsSync('D:\\Program Files\\coopanion\\resources\\app\\mingit\\cmd\\git.exe')
    ? 'D:\\Program Files\\coopanion\\resources\\app\\mingit\\cmd\\git.exe'
    : 'git')

function listFiles() {
  const out = execFileSync(GIT_BIN, ['-c', 'core.quotepath=false', 'ls-files'], { cwd: ROOT, encoding: 'utf8' })
  // 只发布 PwA 自己的东西：工作区后来混进了 _coopanion、cortico-world-memory 等大目录。
  const allow = /^(PwA\/|cortico-world-pwa\/|docs\/|tools\/|README\.md$|LICENSE$|THIRD_PARTY_NOTICES\.md$|VERSIONS\.md$|\.gitignore$)/
  return out
    .split(/\r?\n/)
    .filter(Boolean)
    .filter((path) => allow.test(path))
    .filter((path) => !path.includes('/node_modules/'))
    .filter((path) => existsSync(join(ROOT, path)))
}

async function main() {
  const me = await api('/user')
  const owner = me.login
  console.log(`[publish] owner=${owner} repo=${REPO}`)

  let repo
  try {
    repo = await api(`/repos/${owner}/${REPO}`)
    console.log(`[publish] 仓库已存在：${repo.html_url}`)
  } catch (error) {
    if (error.status !== 404) throw error
    repo = await api('/user/repos', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        name: REPO,
        description: 'Play with Agent — 杀戮尖塔 2 的本机桥接 Mod + Coopanion World（单人 / 本机双窗口联机）',
        private: false,
        has_issues: true,
        has_wiki: false,
        auto_init: false,
      }),
    })
    console.log(`[publish] 仓库已创建：${repo.html_url}`)
  }

  const files = listFiles()
  console.log(`[publish] 文件数 ${files.length}`)

  if (DRY) {
    for (const path of files) console.log('  ' + path)
    return
  }

  // 父提交（仓库已有内容时）
  let parentSha = null
  try {
    const ref = await api(`/repos/${owner}/${REPO}/git/ref/heads/${BRANCH}`)
    parentSha = ref.object.sha
  } catch (error) {
    if (error.status !== 404 && error.status !== 409) throw error
  }

  // 空仓库不能直接用 Git Data API：先用 Contents API 建一个初始提交。
  if (!parentSha) {
    try {
      await api(`/repos/${owner}/${REPO}/contents/README.md`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          message: 'chore: init repository',
          content: Buffer.from(`# PwA\n\n初始化中，发布提交即将覆盖。\n`).toString('base64'),
          branch: BRANCH,
        }),
      })
    } catch (error) {
      if (error.status !== 422) throw error
    }
    const ref = await api(`/repos/${owner}/${REPO}/git/ref/heads/${BRANCH}`)
    parentSha = ref.object.sha
    console.log(`[publish] 初始提交 ${parentSha}`)
  }

  const blobs = []
  for (const path of files) {
    const content = readFileSync(join(ROOT, path))
    const blob = await api(`/repos/${owner}/${REPO}/git/blobs`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ content: content.toString('base64'), encoding: 'base64' }),
    })
    blobs.push({ path, mode: '100644', type: 'blob', sha: blob.sha })
    console.log(`[publish] blob ${path} (${content.length} bytes)`)
  }

  const tree = await api(`/repos/${owner}/${REPO}/git/trees`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ tree: blobs }),
  })

  const commit = await api(`/repos/${owner}/${REPO}/git/commits`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      message: MESSAGE,
      tree: tree.sha,
      parents: parentSha ? [parentSha] : [],
    }),
  })
  console.log(`[publish] commit ${commit.sha}`)

  if (parentSha) {
    await api(`/repos/${owner}/${REPO}/git/refs/heads/${BRANCH}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sha: commit.sha, force: true }),
    })
  } else {
    await api(`/repos/${owner}/${REPO}/git/refs`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ref: `refs/heads/${BRANCH}`, sha: commit.sha }),
    })
  }
  await api(`/repos/${owner}/${REPO}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ default_branch: BRANCH }),
  }).catch(() => {})

  const notesPath = notesFile()
  if (notesPath) console.log(`[publish] 发布说明：${notesPath.slice(ROOT.length + 1)}`)
  const body = notesPath ? readFileSync(notesPath, 'utf8') : `PwA ${TAG}`

  let release
  try {
    release = await api(`/repos/${owner}/${REPO}/releases/tags/${TAG}`)
    console.log(`[publish] release 已存在：${release.html_url}`)
    if (notesPath) {
      // 已存在的 release 用新的说明覆盖，省得手改。
      release = await api(`/repos/${owner}/${REPO}/releases/${release.id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ body }),
      })
      console.log('[publish] release 说明已更新')
    }
  } catch (error) {
    if (error.status !== 404) throw error
    release = await api(`/repos/${owner}/${REPO}/releases`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        tag_name: TAG,
        target_commitish: commit.sha,
        name: `PwA ${TAG}`,
        body,
        draft: false,
        prerelease: false,
      }),
    })
    console.log(`[publish] release 已创建：${release.html_url}`)
  }

  const releaseDir = join(ROOT, 'release')
  const existingAssets = new Set((release.assets ?? []).map((asset) => asset.name))
  for (const name of readdirSync(releaseDir).filter((item) => item.endsWith(`-${version}.zip`))) {
    if (existingAssets.has(name)) {
      console.log(`[publish] 资产已存在，跳过 ${name}`)
      continue
    }
    const data = readFileSync(join(releaseDir, name))
    const uploaded = await fetch(`https://uploads.github.com/repos/${owner}/${REPO}/releases/${release.id}/assets?name=${encodeURIComponent(name)}`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${TOKEN}`,
        Accept: 'application/vnd.github+json',
        'User-Agent': 'PwA-release',
        'Content-Type': 'application/octet-stream',
        'Content-Length': String(data.length),
      },
      body: data,
    })
    if (!uploaded.ok) {
      console.error(`[publish] 资产上传失败 ${name}: ${uploaded.status} ${await uploaded.text()}`)
    } else {
      console.log(`[publish] 资产已上传 ${name} (${data.length} bytes)`)
    }
  }

  console.log(`[publish] 完成：${repo.html_url}`)
  console.log(`[publish] release：${release.html_url}`)
}

main().catch((error) => {
  console.error('[publish] 失败:', error.message)
  if (error.body) console.error(JSON.stringify(error.body).slice(0, 500))
  process.exit(1)
})
