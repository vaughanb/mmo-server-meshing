# Contributing to Open MMORPG

The master branch will always reflect the latest release, while development for the next release is queued in the develop branch. **You should create your own feature branch and pull request into develop.** All changes to develop and master (on release) branches require a pull request.

1. **Start with an Issue**. Give it a sensible name so that when a feature branch is created, you can tell what the branch is about.

2. Tag the issue with a label like scalability, security or stability.

3. Under Development, click **Create a branch** and base it off of **develop**.

4. When you are done working on the issue, open a Pull Request from your branch into **develop**.

### Structure

Open MMORPG is assembled from many source repos. The directory structure is flattened for use with `git subtree`, instead of submodules, and placed into a sensible structure:

- `Core`, `MMO`, `Server`, `Database`, and `SharedData` hold the kit itself
- repos previously nested under Core live in `ThirdParty`
- `Tools` holds Open MMORPG specific tooling such as the Addon Manager

This repository was forked from [MmoKitCE](https://github.com/denariigames/MmoKitCE) and keeps its full history. The upstream forks used by MmoKitCE remain valid sources; each has an `upstream` branch where modifications from the original source repo can occur. Setting up the remotes looks like this:

```sh
$ git remote add mmokitce https://github.com/denariigames/MmoKitCE.git
$ git remote add core https://github.com/denariigames/UnityMultiplayerARPG_Core.git
$ git remote add mmo https://github.com/denariigames/UnityMultiplayerARPG_MMO.git
$ git remote add mmosrv https://github.com/denariigames/UnityMultiplayerARPG_MMOSource.git
$ git remote add mmodb https://github.com/denariigames/UnityMultiplayerARPG_DatabaseManagerSource.git
$ git remote add shared https://github.com/denariigames/UnityMultiplayerARPG_SharedData.git
$ git remote add aat https://github.com/denariigames/unity-addressable-asset-tools.git
$ git remote add audm https://github.com/denariigames/unity-audio-manager.git
$ git remote add cam https://github.com/denariigames/unity-camera-and-input.git
$ git remote add devex https://github.com/denariigames/unity-dev-extension.git
$ git remote add litenet https://github.com/denariigames/LiteNetLibManager.git
$ git remote add rest https://github.com/denariigames/unity-rest-client.git
$ git remote add scb https://github.com/denariigames/SerializableCallback.git
$ git remote add sps https://github.com/denariigames/unity-spatial-partitioning-systems.git
$ git remote add ueu https://github.com/denariigames/unity-editor-utils.git
$ git remote add ugs https://github.com/denariigames/unity-graphic-settings.git
$ git remote add uss https://github.com/denariigames/unity-serialization-surrogates.git
$ git remote add uum https://github.com/denariigames/unity-update-manager.git
$ git remote add xnode https://github.com/denariigames/xNode.git
```

### Updating from Source Repos

To pull the latest changes from MmoKitCE, merge its master or develop branch:

```sh
$ git fetch mmokitce
$ git merge mmokitce/develop
```

To pull the latest changes from an individual upstream repo into its subtree:

```sh
$ git subtree pull --prefix=Core core upstream
$ git subtree pull --prefix=ThirdParty/AudioManager audm upstream
...etc
```

### Releasing

1. Merge develop into master and tag the release (for example `v1.0.0`).
2. Rebuild `OpenMMORPG.unitypackage` and `OpenMMORPG_Settings.unitypackage`, bump the version in `package.json`, and commit them to the [installer repository](https://github.com/open-mmorpg/open-mmorpg-installer).
3. Update the version in `Resources/BuildInfo.asset`.
