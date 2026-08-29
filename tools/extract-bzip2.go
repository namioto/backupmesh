//go:build ignore

package main

import (
	"compress/bzip2"
	"fmt"
	"io"
	"os"
)

func main() {
	if len(os.Args) != 3 {
		fmt.Fprintln(os.Stderr, "usage: extract-bzip2 <input> <output>")
		os.Exit(2)
	}
	input, err := os.Open(os.Args[1])
	if err != nil {
		panic(err)
	}
	defer input.Close()
	output, err := os.OpenFile(os.Args[2], os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o755)
	if err != nil {
		panic(err)
	}
	if _, err = io.Copy(output, bzip2.NewReader(input)); err != nil {
		output.Close()
		panic(err)
	}
	if err = output.Close(); err != nil {
		panic(err)
	}
}
