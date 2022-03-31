#!/bin/bash

PACKAGE_ROOT=$(ls -d1 ~/.nuget/packages/microsoft.playwright/* | head -n 1)

cp $PACKAGE_ROOT/buildTransitive/playwright.ps1 .
cp $PACKAGE_ROOT/lib/netstandard2.0/Microsoft.Playwright.dll .
cp -r $PACKAGE_ROOT/.playwright .

pwsh playwright.ps1 install chromium
