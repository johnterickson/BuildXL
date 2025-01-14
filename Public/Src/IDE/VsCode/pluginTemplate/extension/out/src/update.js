// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
'use strict';
Object.defineProperty(exports, "__esModule", { value: true });
// This must be the first statement otherwise modules might get loaded with
// the wrong locale.
const nls = require("vscode-nls");
const vscode_1 = require("vscode");
nls.config({ locale: vscode_1.env.language });
const path = require("path");
const fs = require("fs");
const child_process_1 = require("child_process");
function copyBuildVersion(arg) {
    return { date: arg.date, index: arg.index, minor: arg.minor, };
}
function needUpdate(ours, theirs) {
    if (theirs.date > ours.date) {
        return true;
    }
    if (theirs.date === ours.date) {
        if (theirs.index > ours.index) {
            return true;
        }
        if (theirs.index === ours.index) {
            if (theirs.minor > ours.minor) {
                return true;
            }
        }
    }
    return false;
}
const baseExtensionInstallPath = "//fsu/shares/mseng/Domino/Extensions";
function checkForUpdates() {
    return new Promise(function (resolve, reject) {
        let checkForUpdate = true;
        let latestUpdatePath = undefined;
        let ourBuildVersion = {
            date: 0,
            index: 0,
            minor: 0
        };
        let theirBuildVersion = {
            date: 0,
            index: 0,
            minor: 0
        };
        // Our build version is in the form of "date.index.minor"
        const thisExtension = vscode_1.extensions.getExtension("Microsoft.dscript");
        const thisVersion = thisExtension.packageJSON.version;
        const splitVersion = thisVersion.split('.');
        if (splitVersion.length === 3) {
            ourBuildVersion.date = Number(splitVersion[0]);
            ourBuildVersion.index = Number(splitVersion[1]);
            ourBuildVersion.minor = Number(splitVersion[2]);
            // If we can't parse our version number, then bail
            if (isNaN(ourBuildVersion.date) || isNaN(ourBuildVersion.index) || isNaN(ourBuildVersion.minor)) {
                checkForUpdate = false;
            }
            // If our version numbers are all zero, then this is the development version
            if (ourBuildVersion.date === 0 && ourBuildVersion.index === 0 && ourBuildVersion.minor === 0) {
                checkForUpdate = false;
            }
        }
        if (checkForUpdate) {
            fs.readdir(baseExtensionInstallPath, (err, files) => {
                if (!files) {
                    return;
                }
                files.forEach((f, index) => {
                    // The versions on the share are in the form of "0.date.index.minor"
                    const split = f.split('.');
                    if (split.length === 4) {
                        theirBuildVersion.date = Number(split[1]);
                        theirBuildVersion.index = Number(split[2]);
                        theirBuildVersion.minor = Number(split[3]);
                        // We don't stop the loop so we get the largest possible upgrade
                        if (!isNaN(theirBuildVersion.date) && !isNaN(theirBuildVersion.index) && !isNaN(theirBuildVersion.minor) && needUpdate(ourBuildVersion, theirBuildVersion)) {
                            ourBuildVersion = copyBuildVersion(theirBuildVersion);
                            latestUpdatePath = f;
                        }
                    }
                });
                if (latestUpdatePath !== undefined) {
                    vscode_1.window.showInformationMessage(`Version ${theirBuildVersion.date}.${theirBuildVersion.index}.${theirBuildVersion.minor} of the BuildXL DScript extension is available`, "Open", "Install").then(optionChosen => {
                        if (optionChosen === "Open") {
                            const uriToOpen = vscode_1.Uri.file(`${baseExtensionInstallPath}/${latestUpdatePath}`).toString();
                            child_process_1.spawn("cmd", ["/c", "start", uriToOpen]);
                        }
                        else if (optionChosen == "Install") {
                            fs.readdir(`${baseExtensionInstallPath}/${latestUpdatePath}`, (err, files) => {
                                if (!files) {
                                    return;
                                }
                                for (let file of files) {
                                    if (file.toLowerCase().startsWith("dscriptvscodeextension.")) {
                                        const vscodeProcessPath = path.dirname(process.execPath);
                                        const vsCpdeCommandFile = path.join(vscodeProcessPath, "bin", "code.cmd");
                                        const childProcess = child_process_1.spawn("cmd", ["/c", vsCpdeCommandFile, "--install-extension", `${baseExtensionInstallPath}/${latestUpdatePath}/${file}`]);
                                        childProcess.on("close", (code, signal) => {
                                            if (code === 0) {
                                                vscode_1.window.showInformationMessage("Extension updated successfully. Reload window to take effect");
                                            }
                                            else {
                                                vscode_1.window.showInformationMessage(`Extension failed to updated. Error code: ${code}`);
                                            }
                                        });
                                        break;
                                    }
                                }
                            });
                        }
                    });
                }
            });
        }
    });
}
exports.checkForUpdates = checkForUpdates;
//# sourceMappingURL=update.js.map