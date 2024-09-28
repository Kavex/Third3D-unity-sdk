import * as core from '@actions/core'
import * as github from '@actions/github'

main()

async function main() {
    const package_name = core.getInput("package");
    const index = {
        name: core.getInput("name"),
        author: core.getInput("author"),
        url: core.getInput("url"),
        id: core.getInput("id"),
        packages: {
            [package_name]: {
                versions: {} as { [version: string]: object }
            }
        }
    }

    const token = core.getInput('token')
    const octokit = github.getOctokit(token)
    const releases = await (
        await octokit.rest.repos.listReleases(github.context.repo)
    ).data
    for (const release of releases) {
        console.log(release.assets)
        var package_asset = release.assets.find(
            asset => asset.name === 'package.json'
        )
        console.log(package_asset)
        if (package_asset === undefined) continue;
        var package_zip = release.assets.find(asset => asset.name.endsWith('.zip'))
        if (package_zip === undefined) continue;

        var package_data = (await octokit.rest.repos
            .getReleaseAsset({
                ...github.context.repo,
                asset_id: package_asset.id,
                headers: { accept: 'application/octet-stream' }
            })
            .then(resp => resp.data)) as ArrayBuffer

        var package_json = JSON.parse(new TextDecoder('utf-8').decode(package_data))

        package_json.url = package_zip.browser_download_url

        if (package_json.version in index.packages[package_name].versions) continue;

        index.packages[package_name].versions[(package_json.version) as string] = package_json;
    }
    core.setOutput("index", JSON.stringify(index));
}
