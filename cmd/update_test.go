package cmd

import "testing"

func TestFindAsset(t *testing.T) {
	assets := []ghAsset{
		{Name: "unity-cli-linux-amd64"},
		{Name: "unity-cli-darwin-arm64"},
		{Name: "unity-cli-windows-amd64.exe"},
	}

	tests := []struct {
		goos, goarch string
		want         string
	}{
		{"linux", "amd64", "unity-cli-linux-amd64"},
		{"linux", "arm64", ""},
		{"darwin", "arm64", "unity-cli-darwin-arm64"},
		{"windows", "amd64", "unity-cli-windows-amd64.exe"},
	}
	for _, tt := range tests {
		t.Run(tt.goos+"/"+tt.goarch, func(t *testing.T) {
			got := findAssetFor(assets, tt.goos, tt.goarch)
			if tt.want == "" {
				if got != nil {
					t.Errorf("findAssetFor(%s, %s) = %q, want no match", tt.goos, tt.goarch, got.Name)
				}
				return
			}
			if got == nil || got.Name != tt.want {
				t.Errorf("findAssetFor(%s, %s) = %v, want %q", tt.goos, tt.goarch, got, tt.want)
			}
		})
	}

	if got := findAssetFor(nil, "linux", "amd64"); got != nil {
		t.Error("findAssetFor: should return nil for an empty list")
	}
	if got := findAssetFor([]ghAsset{{Name: "unity-cli-plan9-mips"}}, "linux", "amd64"); got != nil {
		t.Error("findAssetFor: should return nil when no platform matches")
	}
}
