.PHONY: pack publish

generate:
	pwsh scripts/Generate-Message-Sources.ps1

pack:
	dotnet pack -c Release

publish: pack
	# dotnet nuget push QuickFIXn/bin/Release/QuickFIXn.Core.artex.1.11.102.nupkg  --source https://api.nuget.org/v3/index.json --api-key $(NUGET_API_KEY)
	# dotnet nuget push Messages/FIXT11/bin/Release/QuickFIXn.FIXT1.1.artex.1.11.102.nupkg  --source https://api.nuget.org/v3/index.json --api-key $(NUGET_API_KEY)
	# dotnet nuget push Messages/FIX50SP2artex/bin/Release/QuickFIXn.FIX5.0SP2.artex.1.11.102.nupkg  --source https://api.nuget.org/v3/index.json --api-key $(NUGET_API_KEY)


publish_local: pack
	dotnet nuget push QuickFIXn/bin/Release/QuickFIXn.Core.artex.1.11.102.nupkg --source $(HOME)/.nuget/local_repo/
	dotnet nuget push Messages/FIXT11/bin/Release/QuickFIXn.FIXT1.1.artex.1.11.102.nupkg --source $(HOME)/.nuget/local_repo/
	dotnet nuget push Messages/FIX50SP2artex/bin/Release/QuickFIXn.FIX5.0SP2.artex.1.11.102.nupkg --source $(HOME)/.nuget/local_repo/
