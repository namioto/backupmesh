package controlapi

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"net"
	"strconv"
	"time"
)

type NearbyAnnouncement struct {
	AgentID   string
	AgentName string
	Identity  string
	PublicKey string
}

type NearbyStorage struct {
	Host            string
	Port            int
	StorageIdentity string
	RequestIDs      []string
}

type nearbyMessage struct {
	Protocol        string   `json:"protocol"`
	Nonce           string   `json:"nonce"`
	Port            int      `json:"port"`
	AgentID         string   `json:"agent_id"`
	AgentName       string   `json:"agent_name"`
	Identity        string   `json:"identity"`
	PublicKey       string   `json:"public_key"`
	StorageIdentity string   `json:"storage_identity,omitempty"`
	RequestIDs      []string `json:"request_ids,omitempty"`
}

// AnnounceNearby sends public identity only. Returned addresses remain untrusted until HTTPS
// presents the advertised certificate fingerprint.
func AnnounceNearby(ctx context.Context, announcement NearbyAnnouncement) ([]NearbyStorage, error) {
	socket, err := net.ListenUDP("udp4", &net.UDPAddr{})
	if err != nil {
		return nil, err
	}
	defer socket.Close()
	stop := context.AfterFunc(ctx, func() { _ = socket.Close() })
	defer stop()
	nonce := make([]byte, 16)
	if _, err := rand.Read(nonce); err != nil {
		return nil, err
	}
	query := nearbyMessage{Protocol: "backupmesh-pairing-v2", Nonce: hex.EncodeToString(nonce), AgentID: announcement.AgentID, AgentName: announcement.AgentName, Identity: announcement.Identity, PublicKey: announcement.PublicKey}
	packet, _ := json.Marshal(query)
	for _, destination := range lanBroadcasts() {
		_, _ = socket.WriteToUDP(packet, &destination)
	}
	deadline := time.Now().Add(2 * time.Second)
	if contextDeadline, ok := ctx.Deadline(); ok && contextDeadline.Before(deadline) {
		deadline = contextDeadline
	}
	_ = socket.SetReadDeadline(deadline)
	buffer := make([]byte, 2049)
	seen := map[string]bool{}
	var found []NearbyStorage
	for {
		n, sender, err := socket.ReadFromUDP(buffer)
		if err != nil {
			if ctx.Err() != nil {
				return found, ctx.Err()
			}
			if netError, ok := err.(net.Error); ok && netError.Timeout() {
				return found, nil
			}
			return found, err
		}
		var reply nearbyMessage
		if n > 2048 || sender.Port != discoveryPort || json.Unmarshal(buffer[:n], &reply) != nil || reply.Protocol != query.Protocol || reply.Nonce != query.Nonce || reply.Port < 1 || reply.Port > 65535 || len(reply.StorageIdentity) != 64 || len(reply.RequestIDs) > 16 {
			continue
		}
		if _, err := hex.DecodeString(reply.StorageIdentity); err != nil {
			continue
		}
		key := net.JoinHostPort(sender.IP.String(), strconv.Itoa(reply.Port))
		if seen[key] {
			continue
		}
		seen[key] = true
		found = append(found, NearbyStorage{Host: sender.IP.String(), Port: reply.Port, StorageIdentity: reply.StorageIdentity, RequestIDs: reply.RequestIDs})
		if len(found) == 16 {
			return found, nil
		}
	}
}
