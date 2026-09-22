package controlapi

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net"
	"runtime"
	"strconv"
	"sync"
	"time"
)

const (
	discoveryPort      = 7445
	linuxDiscoveryPort = 7446
)

var linuxDiscoveryTurn = make(chan struct{}, 1)

func openDiscoverySocket(ctx context.Context) (*net.UDPConn, func(), error) {
	address := &net.UDPAddr{}
	if runtime.GOOS != "linux" {
		socket, err := net.ListenUDP("udp4", address)
		return socket, func() { _ = socket.Close() }, err
	}
	if err := ctx.Err(); err != nil {
		return nil, nil, err
	}
	select {
	case <-ctx.Done():
		return nil, nil, ctx.Err()
	case linuxDiscoveryTurn <- struct{}{}:
	}
	address.Port = linuxDiscoveryPort
	socket, err := net.ListenUDP("udp4", address)
	if err != nil {
		<-linuxDiscoveryTurn
		return nil, nil, err
	}
	return socket, func() { _ = socket.Close(); <-linuxDiscoveryTurn }, nil
}

type discoveryMessage struct {
	Protocol    string `json:"protocol"`
	Fingerprint string `json:"fingerprint"`
	Nonce       string `json:"nonce"`
	Port        int    `json:"port"`
}

type lanDialer struct {
	mu          sync.Mutex
	host        string
	controlPort string
	fingerprint string
	tlsConfig   *tls.Config
}

func (d *lanDialer) connect(ctx context.Context, host, port string) (net.Conn, error) {
	attempt, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()
	dialer := tls.Dialer{NetDialer: &net.Dialer{}, Config: d.tlsConfig}
	return dialer.DialContext(attempt, "tcp", net.JoinHostPort(host, port))
}

// Only fully authenticated TLS connections update the cached address. In particular a UDP
// response, even one advertising the right fingerprint, cannot redirect credentials.
func (d *lanDialer) DialTLSContext(ctx context.Context, network, address string) (net.Conn, error) {
	_, port, err := net.SplitHostPort(address)
	if err != nil {
		return nil, err
	}
	d.mu.Lock()
	host := d.host
	d.mu.Unlock()
	conn, err := d.connect(ctx, host, port)
	if err == nil {
		return conn, nil
	}
	ctx, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()
	return discoverStorage(ctx, d.fingerprint, d.controlPort, lanBroadcasts(), func(host string) (net.Conn, error) {
		conn, err := d.connect(ctx, host, port)
		if err == nil {
			d.mu.Lock()
			d.host = host
			d.mu.Unlock()
		}
		return conn, err
	})
}

func discoverStorage(ctx context.Context, fingerprint, controlPort string, destinations []net.UDPAddr, connect func(string) (net.Conn, error)) (net.Conn, error) {
	socket, closeSocket, err := openDiscoverySocket(ctx)
	if err != nil {
		return nil, err
	}
	defer closeSocket()
	stop := context.AfterFunc(ctx, func() { socket.Close() })
	defer stop()
	nonce := make([]byte, 16)
	if _, err := rand.Read(nonce); err != nil {
		return nil, err
	}
	query := discoveryMessage{Protocol: "backupmesh-discovery-v1", Fingerprint: fingerprint, Nonce: hex.EncodeToString(nonce)}
	packet, _ := json.Marshal(query)
	for _, destination := range destinations {
		_, _ = socket.WriteToUDP(packet, &destination)
	}
	buffer := make([]byte, 513)
	seen := make(map[string]bool)
	for {
		n, sender, err := socket.ReadFromUDP(buffer)
		if err != nil {
			return nil, fmt.Errorf("paired Storage is not reachable on this LAN: %w", err)
		}
		validPort := false
		for _, destination := range destinations {
			if sender.Port == destination.Port {
				validPort = true
				break
			}
		}
		if !validPort {
			continue
		}
		var reply discoveryMessage
		if n > 512 || json.Unmarshal(buffer[:n], &reply) != nil ||
			reply.Protocol != query.Protocol || reply.Fingerprint != fingerprint || reply.Nonce != query.Nonce || strconv.Itoa(reply.Port) != controlPort {
			continue
		}
		host := sender.IP.String()
		if seen[host] {
			continue
		}
		seen[host] = true
		if conn, err := connect(host); err == nil {
			return conn, nil
		}
	}
}

func lanBroadcasts() []net.UDPAddr {
	var destinations []net.UDPAddr
	interfaces, err := net.Interfaces()
	if err != nil {
		return nil
	}
	for _, adapter := range interfaces {
		if adapter.Flags&net.FlagUp == 0 || adapter.Flags&net.FlagBroadcast == 0 || adapter.Flags&net.FlagLoopback != 0 {
			continue
		}
		addresses, _ := adapter.Addrs()
		for _, address := range addresses {
			subnet, ok := address.(*net.IPNet)
			if !ok || subnet.IP.To4() == nil || len(subnet.Mask) != net.IPv4len {
				continue
			}
			broadcast := append(net.IP(nil), subnet.IP.To4()...)
			for i := range broadcast {
				broadcast[i] |= ^subnet.Mask[i]
			}
			destinations = append(destinations, net.UDPAddr{IP: broadcast, Port: discoveryPort})
		}
	}
	return destinations
}

func certificateFingerprint(raw []byte) string {
	sum := sha256.Sum256(raw)
	return hex.EncodeToString(sum[:])
}
