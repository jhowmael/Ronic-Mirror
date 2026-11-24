package main

import (
	"fmt"
	"net"
	"sort"
	"strings"
)

// Interfaces virtuais/VPN que normalmente NÃO são a rede Wi-Fi onde está o iPad.
var ignoredIfacePrefixes = []string{
	"lo", "utun", "awdl", "llw", "bridge", "vmnet", "vboxnet", "docker", "veth",
	"tun", "tap", "gif", "stf", "anpi", "ap", "vEthernet", "VirtualBox", "VMware",
}

type candidateIP struct {
	ip    net.IP
	iface string
	score int
}

// localIPv4s devolve os IPv4 locais ordenados do mais provável (Wi-Fi/LAN) ao menos provável.
func localIPv4s() []string {
	ifaces, err := net.Interfaces()
	if err != nil {
		return nil
	}
	var cands []candidateIP
	for _, ifc := range ifaces {
		if ifc.Flags&net.FlagUp == 0 || ifc.Flags&net.FlagLoopback != 0 || ignored(ifc.Name) {
			continue
		}
		addrs, err := ifc.Addrs()
		if err != nil {
			continue
		}
		for _, a := range addrs {
			ipnet, ok := a.(*net.IPNet)
			if !ok {
				continue
			}
			ip := ipnet.IP.To4()
			if ip == nil || ip.IsLinkLocalUnicast() || ip.IsLoopback() {
				continue
			}
			cands = append(cands, candidateIP{ip: ip, iface: ifc.Name, score: score(ip, ifc.Name)})
		}
	}
	sort.SliceStable(cands, func(i, j int) bool { return cands[i].score > cands[j].score })
	out := make([]string, 0, len(cands))
	for _, c := range cands {
		out = append(out, c.ip.String())
	}
	return out
}

func ignored(name string) bool {
	for _, p := range ignoredIfacePrefixes {
		if strings.HasPrefix(name, p) {
			return true
		}
	}
	return false
}

func score(ip net.IP, iface string) int {
	s := 0
	switch {
	case ip[0] == 192 && ip[1] == 168:
		s += 30
	case ip[0] == 10:
		s += 20
	case ip[0] == 172 && ip[1] >= 16 && ip[1] <= 31:
		s += 10
	}
	// en0 costuma ser o Wi-Fi no Mac; "Wi-Fi"/"Wireless" no Windows.
	lname := strings.ToLower(iface)
	if iface == "en0" || strings.Contains(lname, "wi-fi") || strings.Contains(lname, "wireless") || strings.HasPrefix(lname, "wl") {
		s += 5
	}
	return s
}

func viewerURLs(port int) []string {
	ips := localIPv4s()
	urls := make([]string, 0, len(ips))
	for _, ip := range ips {
		urls = append(urls, fmt.Sprintf("http://%s:%d", ip, port))
	}
	if len(urls) == 0 {
		urls = append(urls, fmt.Sprintf("http://localhost:%d", port))
	}
	return urls
}
