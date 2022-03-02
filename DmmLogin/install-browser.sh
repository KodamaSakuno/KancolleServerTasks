#!/bin/bash

PACKAGE_ROOT=$(ls -d1 ~/.nuget/packages/microsoft.playwright/*)

cp $PACKAGE_ROOT/buildTransitive/playwright.ps1 .
cp $PACKAGE_ROOT/lib/net5.0/Microsoft.Playwright.dll .
cp -r $PACKAGE_ROOT/.playwright .

pwsh playwright.ps1 install chromium
