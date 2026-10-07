// ranked_link.gsc
// Lien entre les deux joueurs du mod ranked (BO1 Zombies / Plutonium).
//
// Necessite le plugin t5-gsc-utils.dll dans Plutonium\plugins\ (lecture/ecriture de fichiers).
// Les fichiers sont dans Plutonium\storage\t5\ranked\ :
//
//   state.txt     ecrit par le mod, lu par l'app compagnon. Une ligne :
//                 round;zone;a_terre;temps_ms;termine;temps_final_ms;seed;objectif;pret
//   go.txt        ecrit par l'app compagnon quand les deux joueurs sont prets. Contient la seed du match.
//   opponent.txt  ecrit par l'app compagnon, lu par le mod. Une ligne :
//                 round;zone;a_terre;termine
//                 (zone = "none" si inconnue)
//
// Les memes infos existent aussi en dvars, pour tester a la main dans la console.
// Les dvars ranked_opp_* ne sont lues que si opponent.txt n'existe pas.
//
// SORTIE (ecrit par le mod, lu par l'app compagnon) :
//   ranked_my_round        round actuel
//   ranked_my_zone         zone actuelle (ex : foyer_zone)
//   ranked_my_down         1 si a terre
//   ranked_my_time         temps de jeu en secondes
//   ranked_my_finished     1 quand le round objectif est atteint
//   ranked_my_finish_time  temps final en millisecondes
//
// ENTREE (ecrit par l'app compagnon ou a la main dans la console, lu par le mod) :
//   ranked_opp_round       round de l'adversaire (0 = pas d'adversaire, HUD cache)
//   ranked_opp_zone        zone de l'adversaire
//   ranked_opp_down        1 si l'adversaire est a terre
//   ranked_opp_finished    1 quand l'adversaire a atteint le round objectif
//
// REGLAGE :
//   ranked_goal            round a atteindre (30 par defaut)

#include maps\_utility;
#include common_scripts\utility;

rr_link_init()
{
	if ( isDefined( level.rr_link_started ) )
	{
		return;
	}
	level.rr_link_started = true;

	// En match, le debut de partie du jeu (grenades, affichage du round 1, premiers zombies) est
	// retenu jusqu'au GO : le jeu appelle cette fonction juste avant de lancer le round 1.
	if ( fileExists( "ranked/match.txt" ) )
	{
		level.round_prestart_func = ::rr_prestart;
	}

	level thread rr_link_main();
}

// Anti-record : force l'affichage des checksums Plutonium tant que le mod est installe.
// Les scripts modifies changent ces checksums, donc une partie jouee avec le mod est
// reconnaissable sur une video et ne peut pas passer pour une partie normale.
rr_force_checksums()
{
	setDvar( "cg_drawChecksums", 1 );
	setDvar( "cg_flashScriptHashes", 1 );
}

// Reapplique en continu : le joueur ne peut pas les couper depuis la console.
rr_checksum_guard()
{
	while ( 1 )
	{
		rr_force_checksums();
		wait 0.5;
	}
}

rr_prestart()
{
	level.rr_prestart_entered = true;

	while ( get_players().size == 0 || !isDefined( level.rr_goal ) )
	{
		wait 0.05;
	}
	rr_wait_for_go( get_players()[0], level.rr_goal );
	level.rr_go_done = true;

	wait 2;		// delai normal du jeu avant le round 1
}

rr_link_main()
{
	rr_force_checksums();
	level thread rr_checksum_guard();

	// Remise a zero : les dvars gardent leur valeur d'une partie a l'autre.
	setDvar( "ranked_opp_round", 0 );
	setDvar( "ranked_opp_zone", "" );
	setDvar( "ranked_opp_down", 0 );
	setDvar( "ranked_opp_finished", 0 );
	setDvar( "ranked_my_round", 0 );
	setDvar( "ranked_my_zone", "" );
	setDvar( "ranked_my_down", 0 );
	setDvar( "ranked_my_time", 0 );
	setDvar( "ranked_my_finished", 0 );
	setDvar( "ranked_my_finish_time", 0 );

	// Objectif : celui du match en cours (ranked/match.txt = "seed;objectif"), sinon la dvar, sinon 30.
	goal = 0;
	in_match = false;
	if ( fileExists( "ranked/match.txt" ) )
	{
		tokens = strTok( readFile( "ranked/match.txt" ), ";" );
		if ( tokens.size >= 2 )
		{
			goal = int( tokens[1] );
			in_match = true;
			// Map du match (nom du script, ex. zombie_theater). Si le joueur en a lance une autre,
			// il ne peut pas se declarer pret : voir rr_wait_for_go.
			if ( tokens.size >= 3 && tokens[2] != "" && tokens[2] != level.script )
			{
				level.rr_wrong_map = rr_map_name( tokens[2] );
			}
		}
	}

	// Le dossier ranked n'existe que pendant un match : l'app compagnon le cree quand un match est
	// trouve et le supprime a la fin. Hors match, le mod ne lit et n'ecrit aucun fichier.
	files_write = in_match;		// ecriture de state.txt
	files_reads = -1;			// lectures d'opponent.txt restantes (-1 = sans limite)
	if ( in_match )
	{
		if ( fileExists( "ranked/opponent.txt" ) )
		{
			// Evite d'afficher l'adversaire de la partie precedente.
			removeFile( "ranked/opponent.txt" );
		}
		if ( fileExists( "ranked/go.txt" ) )
		{
			removeFile( "ranked/go.txt" );
		}
		writeFile( "ranked/state.txt", "0;none;0;0;0;0;0;0;0" );
	}
	if ( goal <= 0 )
	{
		goal = getDvarInt( "ranked_goal" );
	}
	if ( goal <= 0 )
	{
		goal = 30;
	}

	level.rr_goal = goal;

	while ( get_players().size == 0 )
	{
		wait 0.5;
	}
	player = get_players()[0];

	// En match : attente du GO. Normalement le debut de partie est retenu par rr_prestart.
	// Securite : si le jeu a demarre le round 1 sans passer par rr_prestart, les zombies sont
	// bloques ici et l'attente du GO se fait a ce moment-la.
	if ( in_match )
	{
		while ( !isDefined( level.rr_go_done ) )
		{
			player freezeControls( true );
			if ( !isDefined( level.rr_prestart_entered ) && rr_flag_is_set( "begin_spawning" ) )
			{
				flag_clear( "spawn_zombies" );
				rr_wait_for_go( player, goal );
				level.rr_go_done = true;
				if ( isDefined( level.rr_cancelled ) )
				{
					flag_set( "spawn_zombies" );
				}
				else
				{
					level thread rr_spawn_after( 5 );
				}
			}
			wait 0.1;
		}

		if ( isDefined( level.rr_cancelled ) )
		{
			// Match annule avant le depart : la partie continue comme une partie solo normale.
			in_match = false;
			files_write = false;
		}
		else
		{
			level thread rr_settings_watch();
		}
	}
	else
	{
		while ( !rr_flag_is_set( "begin_spawning" ) )
		{
			wait 0.1;
		}
	}

	start_time = getTime();
	if ( in_match && isDefined( level.rr_go_time ) )
	{
		start_time = level.rr_go_time;		// le chrono part au GO
	}

	if ( in_match )
	{
		// Enregistrement du match (anti-triche) : voir rr_record_think.
		seed_now = 0;
		if ( isDefined( level.rr_seed ) )
		{
			seed_now = level.rr_seed;
		}
		level.rr_rec_start = start_time;
		level.rr_rec_on = true;
		writeFile( "ranked/replay.txt", "H;" + seed_now + ";" + goal + "\n" );
		level thread rr_record_think( player );
	}

	hud_round = rr_link_hud( player, 70, 1.4 );
	hud_zone = rr_link_hud( player, 86, 1.2 );
	hud_state = rr_link_hud( player, 100, 1.2 );
	hud_result = rr_center_hud( player, -70, 2.4 );		// resultat du match, au centre de l'ecran

	// Chronos de speedrun : temps total depuis le depart, et temps du round en cours.
	hud_time_label = rr_link_hud( player, 126, 1.2 );
	hud_time_label setText( "Time" );
	level.rr_hud_time = rr_link_hud( player, 124, 1.5 );
	level.rr_hud_time.x = 62;
	level.rr_hud_time setTimerUp( 0 );
	hud_split_label = rr_link_hud( player, 144, 1.2 );
	hud_split_label setText( "Round" );
	level.rr_hud_split = rr_link_hud( player, 142, 1.5 );
	level.rr_hud_split.x = 62;
	level.rr_timers_stopped = false;
	level.rr_timer_start = start_time;
	level thread rr_round_timer();

	last_round = -1;
	last_zone = "?";
	last_state = -1;
	finished = false;
	lost = false;
	finish_time = 0;
	gave_up = false;		// mort ou abandon : defaite immediate
	opp_out = false;		// l'adversaire est mort, a abandonne ou a quitte : victoire

	level.rr_surrender = false;
	level.rr_dead = false;
	if ( in_match )
	{
		// Abandon : commande "surrender", attachee a la touche F10 pour ne pas passer par la console.
		// (addCommand et executeCommand viennent du plugin t5-gsc-utils.)
		addCommand( "surrender", ::rr_cmd_surrender );
		executeCommand( "bind F10 surrender" );
		level thread rr_watch_game_over();
	}

	opp_round = 0;
	opp_zone = "";
	opp_down = 0;
	opp_finished = 0;

	while ( 1 )
	{
		// ---------------- mon etat -> dvars ----------------
		my_round = 0;
		if ( isDefined( level.round_number ) )
		{
			my_round = level.round_number;
		}

		my_zone = player maps\_zombiemode_utility::get_current_zone();
		if ( !isDefined( my_zone ) )
		{
			my_zone = "";
		}

		my_down = 0;
		if ( player maps\_laststand::player_is_in_laststand() )
		{
			my_down = 1;
		}

		elapsed = getTime() - start_time;

		setDvar( "ranked_my_round", my_round );
		setDvar( "ranked_my_zone", my_zone );
		setDvar( "ranked_my_down", my_down );

		if ( !finished )
		{
			setDvar( "ranked_my_time", int( elapsed / 1000 ) );

			if ( in_match && my_round >= goal && !gave_up )
			{
				finished = true;
				setDvar( "ranked_my_finished", 1 );
				setDvar( "ranked_my_finish_time", elapsed );
				finish_time = elapsed;
				rr_stop_timers( elapsed );
				rr_record_sample( player );		// le round objectif doit figurer dans l'enregistrement

				if ( !lost )
				{
					hud_result.color = ( 0.3, 1, 0.3 );
					hud_result setText( "VICTORY - round " + goal + " in " + rr_format_time( elapsed ) );
					level thread rr_match_over( player );
				}
			}
		}

		// ---------------- reglage de triche detecte ----------------
		if ( in_match && isDefined( level.rr_cheated ) && !finished && !lost && !opp_out && !gave_up )
		{
			gave_up = true;
			rr_event( "FLAG;" + level.rr_cheated );
			hud_result.color = ( 1, 0.3, 0.3 );
			hud_result setText( "DEFEAT - cheat setting used: " + level.rr_cheated );
			level thread rr_match_over( player );
		}

		// ---------------- mort ou abandon ----------------
		if ( in_match && !finished && !lost && !opp_out && !gave_up && ( level.rr_surrender || level.rr_dead ) )
		{
			gave_up = true;
			hud_result.color = ( 1, 0.3, 0.3 );
			if ( level.rr_surrender )
			{
				hud_result setText( "DEFEAT - you surrendered" );
				level thread rr_match_over( player );
			}
			else
			{
				// Mort : le jeu lance deja sa propre fin de partie et son retour au menu.
				hud_result setText( "DEFEAT - you died" );
			}
		}

		// ---------------- mon etat -> fichier ----------------
		file_zone = my_zone;
		if ( file_zone == "" )
		{
			file_zone = "none";
		}

		file_time = elapsed;
		file_finished = 0;
		if ( finished )
		{
			file_time = finish_time;
			file_finished = 1;
		}
		if ( gave_up )
		{
			file_finished = 2;		// 2 = elimine (mort ou abandon)
		}

		seed = 0;
		if ( isDefined( level.rr_seed ) )
		{
			seed = level.rr_seed;
		}

		if ( files_write )
		{
			writeFile( "ranked/state.txt", my_round + ";" + file_zone + ";" + my_down + ";" + file_time + ";" + file_finished + ";" + finish_time + ";" + seed + ";" + goal + ";1" );
		}

		// ---------------- etat adverse -> HUD ----------------
		// En match : fichier ecrit par l'app. Hors match : dvars, pour tester a la main dans la console.
		if ( in_match )
		{
			if ( files_reads != 0 && fileExists( "ranked/opponent.txt" ) )
			{
				// Si le fichier est lu pendant que l'app l'ecrit, il peut etre incomplet :
				// dans ce cas on garde les valeurs precedentes.
				tokens = strTok( readFile( "ranked/opponent.txt" ), ";" );
				if ( tokens.size >= 4 )
				{
					opp_round = int( tokens[0] );
					opp_zone = tokens[1];
					if ( opp_zone == "none" )
					{
						opp_zone = "";
					}
					opp_down = int( tokens[2] );
					opp_finished = int( tokens[3] );
				}
			}

			// Fin du match : l'app supprime match.txt, puis tout le dossier quelques secondes plus tard.
			// Le mod arrete d'ecrire tout de suite (sinon il recreerait le dossier) et lit encore
			// deux fois l'etat adverse, pour ne pas manquer le resultat final.
			if ( files_write && !fileExists( "ranked/match.txt" ) )
			{
				files_write = false;
				files_reads = 2;
				level.rr_rec_on = false;
			}
			else if ( files_reads > 0 )
			{
				files_reads--;
			}
		}
		else
		{
			opp_round = getDvarInt( "ranked_opp_round" );
			opp_zone = getDvar( "ranked_opp_zone" );
			opp_down = getDvarInt( "ranked_opp_down" );
			opp_finished = getDvarInt( "ranked_opp_finished" );
		}

		if ( opp_round != last_round )
		{
			last_round = opp_round;
			if ( opp_round > 0 )
			{
				hud_round setText( "Opponent: round " + opp_round );
			}
			else
			{
				hud_round setText( "" );
			}
		}

		if ( opp_zone != last_zone )
		{
			last_zone = opp_zone;
			hud_zone setText( rr_zone_name( opp_zone ) );
		}

		if ( opp_down != last_state )
		{
			last_state = opp_down;
			if ( opp_down > 0 )
			{
				hud_state.color = ( 1, 0.3, 0.3 );
				hud_state setText( "DOWN" );
			}
			else
			{
				hud_state setText( "" );
			}
		}

		// 3 = l'app a vu un fichier de script ou un plugin changer pendant le match : defaite.
		if ( opp_finished == 3 && !finished && !lost && !gave_up && !opp_out )
		{
			gave_up = true;
			hud_result.color = ( 1, 0.3, 0.3 );
			hud_result setText( "DEFEAT - a game file was changed during the match" );
			if ( in_match )
			{
				level thread rr_match_over( player );
			}
		}

		if ( opp_finished == 2 && !finished && !lost && !gave_up && !opp_out )
		{
			opp_out = true;
			hud_result.color = ( 0.3, 1, 0.3 );
			hud_result setText( "VICTORY - your opponent is out" );
			if ( in_match )
			{
				level thread rr_match_over( player );
			}
		}

		if ( opp_finished == 1 && !finished && !lost && !gave_up && !opp_out )
		{
			lost = true;
			hud_result.color = ( 1, 0.3, 0.3 );
			hud_result setText( "DEFEAT - opponent reached round " + goal );
			if ( in_match )
			{
				level thread rr_match_over( player );
			}
		}

		wait 0.5;
	}
}

// Depart synchronise. Le joueur est bloque au spawn, appuie sur une touche quand il est pret,
// et la partie demarre apres un compte a rebours quand l'app compagnon ecrit ranked/go.txt
// (c'est-a-dire quand le serveur a recu "pret" des deux joueurs).
// Si le match est annule (ranked/match.txt supprime par l'app), le joueur est libere sans compte a rebours.
rr_wait_for_go( player, goal )
{
	hud = NewClientHudElem( player );
	hud.foreground = true;
	hud.sort = 2;
	hud.hidewheninmenu = false;
	hud.alignX = "center";
	hud.alignY = "middle";
	hud.horzAlign = "center";
	hud.vertAlign = "middle";
	hud.x = 0;
	hud.y = -60;
	hud.fontScale = 2.2;
	hud.alpha = 1;
	hud.color = ( 1, 1, 1 );
	hud setText( "Press USE or FIRE when you are ready" );
	if ( isDefined( level.rr_wrong_map ) )
	{
		hud.color = ( 1, 0.3, 0.3 );
		hud setText( "Wrong map - this match is on " + level.rr_wrong_map + ". Quit and start it in solo." );
	}

	seed = 0;
	if ( isDefined( level.rr_seed ) )
	{
		seed = level.rr_seed;
	}

	ready = 0;
	cancelled = false;
	ticks = 0;

	while ( 1 )
	{
		player freezeControls( true );

		if ( !ready && !isDefined( level.rr_wrong_map ) && ( player useButtonPressed() || player attackButtonPressed() ) )
		{
			ready = 1;
			hud.color = ( 0.3, 1, 0.3 );
			hud setText( "READY - waiting for your opponent" );
		}

		// Fichiers : toutes les 0.3 s seulement.
		if ( ticks % 3 == 0 )
		{
			writeFile( "ranked/state.txt", "1;none;0;0;0;0;" + seed + ";" + goal + ";" + ready );

			if ( ready && fileExists( "ranked/go.txt" ) )
			{
				break;
			}
			if ( !fileExists( "ranked/match.txt" ) )
			{
				cancelled = true;
				break;
			}
		}

		ticks++;
		wait 0.1;
	}

	if ( cancelled )
	{
		level.rr_cancelled = true;
		hud.color = ( 1, 0.3, 0.3 );
		hud setText( "Match cancelled" );
	}
	else
	{
		hud.color = ( 1, 0.8, 0.2 );
		hud.fontScale = 3;
		for ( count = 3; count > 0; count-- )
		{
			hud setText( "" + count );
			for ( i = 0; i < 10; i++ )
			{
				player freezeControls( true );
				wait 0.1;
			}
		}
		hud.color = ( 0.3, 1, 0.3 );
		hud setText( "GO!" );
	}

	player freezeControls( false );
	level.rr_go_time = getTime();

	hud thread rr_destroy_after( 2 );

	if ( !cancelled )
	{
		iPrintLn( "Press F10 twice to surrender" );
	}
}

// Deux appuis en moins de 3 secondes, pour qu'un appui accidentel ne fasse pas perdre le match.
rr_cmd_surrender( args )
{
	now = getTime();
	if ( isDefined( level.rr_surrender_time ) && now - level.rr_surrender_time < 3000 )
	{
		level.rr_surrender = true;
		return;
	}

	level.rr_surrender_time = now;
	iPrintLnBold( "Press F10 again to surrender" );
}

// Anti-triche : pendant un match, les reglages du jeu qui donnent un avantage doivent garder leur
// valeur normale. La console ne peut pas etre desactivee depuis un script, mais ces reglages sont
// relus 20 fois par seconde : des qu'un seul est modifie, le joueur perd le match.
// Limite connue : une commande de triche tapee et annulee dans la meme ligne de console
// (ex. "sv_cheats 1; give all; sv_cheats 0") n'est pas vue par cette surveillance.
rr_settings_watch()
{
	level endon( "end_game" );

	base_speed = getDvarInt( "g_speed" );		// fixe par le jeu au chargement de la map
	strikes = 0;

	while ( !isDefined( level.rr_match_over ) )
	{
		reason = "";
		if ( getDvarInt( "sv_cheats" ) != 0 )
		{
			reason = "sv_cheats";
		}
		else if ( getDvar( "fs_game" ) != "" )
		{
			reason = "mod loaded";		// un mod charge (Strat Tester...) remplace les scripts du jeu
		}
		else if ( getDvarInt( "developer" ) != 0 )
		{
			reason = "developer";
		}
		else if ( abs( getDvarFloat( "timescale" ) - 1 ) > 0.01 || abs( getDvarFloat( "com_timescale" ) - 1 ) > 0.01 )
		{
			reason = "timescale";
		}
		else if ( getDvarInt( "player_sustainAmmo" ) != 0 )
		{
			reason = "player_sustainAmmo";
		}
		else if ( getDvarInt( "player_sprintUnlimited" ) != 0 )
		{
			reason = "player_sprintUnlimited";
		}
		else if ( getDvarInt( "g_ai" ) != 1 )
		{
			reason = "g_ai";
		}
		else if ( getDvarInt( "ai_disableSpawn" ) != 0 )
		{
			reason = "ai_disableSpawn";
		}
		else if ( getDvar( "magic_chest_movable" ) != rr_expected_chest_movable() )
		{
			reason = "magic_chest_movable";
		}
		else if ( getDvarInt( "jump_height" ) != 39 )
		{
			reason = "jump_height";
		}
		else if ( getDvarInt( "g_speed" ) != base_speed )
		{
			reason = "g_speed";
		}

		if ( reason == "" )
		{
			strikes = 0;
		}
		else
		{
			// Deux lectures de suite, pour ne pas reagir a une valeur lue en plein changement.
			strikes++;
			if ( strikes >= 2 )
			{
				level.rr_cheated = reason;
				return;
			}
		}

		wait 0.05;
	}
}

// Enregistrement du match (anti-triche). Une ligne par seconde dans ranked/replay.txt :
//   S;temps_s;round;points;vie;vie_max;arme;chargeur;reserve;x;y;z;kills;downs
// plus une ligne par evenement (voir rr_event). L'app compagnon envoie le fichier au serveur a la
// fin du match ; le serveur y cherche ce qui ne peut pas arriver dans une partie normale.
rr_record_think( player )
{
	while ( isDefined( level.rr_rec_on ) && level.rr_rec_on )
	{
		rr_record_sample( player );
		wait 1;
	}
}

rr_record_sample( player )
{
	if ( !isDefined( level.rr_rec_on ) || !level.rr_rec_on )
	{
		return;
	}

	t = int( ( getTime() - level.rr_rec_start ) / 1000 );

	round = 0;
	if ( isDefined( level.round_number ) )
	{
		round = level.round_number;
	}

	weapon = player getCurrentWeapon();
	clip = 0;
	stock = 0;
	if ( !isDefined( weapon ) || weapon == "" )
	{
		weapon = "none";
	}
	if ( weapon != "none" )
	{
		clip = player getWeaponAmmoClip( weapon );
		stock = player getWeaponAmmoStock( weapon );
	}

	kills = 0;
	if ( isDefined( player.kills ) )
	{
		kills = player.kills;
	}
	downs = 0;
	if ( isDefined( player.downs ) )
	{
		downs = player.downs;
	}

	appendFile( "ranked/replay.txt", "S;" + t + ";" + round + ";" + player.score + ";" + player.health + ";" + player.maxhealth + ";" + weapon + ";" + clip + ";" + stock + ";" + int( player.origin[0] ) + ";" + int( player.origin[1] ) + ";" + int( player.origin[2] ) + ";" + kills + ";" + downs + "\n" );
}

// Evenement dans l'enregistrement. Appele aussi depuis _zombiemode_weapons.gsc :
//   GIVE;arme;box    arme sortie de la box
//   GIVE;arme;buy    arme achetee (mur) ou donnee par le jeu
//   FLAG;motif       reglage de triche detecte
rr_event( text )
{
	if ( !isDefined( level.rr_rec_on ) || !level.rr_rec_on )
	{
		return;
	}
	t = int( ( getTime() - level.rr_rec_start ) / 1000 );
	appendFile( "ranked/replay.txt", "E;" + t + ";" + text + "\n" );
}

// Sur Nacht der Untoten la box ne bouge jamais : le jeu y met lui-meme ce reglage a 0.
rr_expected_chest_movable()
{
	if ( level.script == "zombie_cod5_prototype" )
	{
		return "0";
	}
	return "1";
}

rr_map_name( script )
{
	switch ( script )
	{
		case "zombie_theater":			return "Kino der Toten";
		case "zombie_cod5_prototype":	return "Nacht der Untoten";
		case "zombie_cod5_asylum":		return "Verruckt";
	}
	return script;
}

// Fin de partie (mort du joueur en solo) = defaite.
rr_watch_game_over()
{
	level waittill( "end_game" );
	level.rr_dead = true;
}

rr_spawn_after( seconds )
{
	wait seconds;
	if ( !isDefined( level.rr_match_over ) )
	{
		flag_set( "spawn_zombies" );
	}
}

// Chrono du round : repart de zero au debut de chaque round, et affiche le temps final du round
// entre deux rounds.
rr_round_timer()
{
	level endon( "end_game" );

	while ( !level.rr_timers_stopped )
	{
		round_start = getTime();
		level.rr_hud_split setTimerUp( 0 );

		level waittill( "end_of_round" );
		if ( level.rr_timers_stopped )
		{
			return;
		}
		level.rr_hud_split setText( rr_format_time( getTime() - round_start ) );

		level waittill( "start_of_round" );
	}
}

// Fin du match : les deux chronos se figent.
rr_stop_timers( elapsed )
{
	if ( !isDefined( level.rr_timers_stopped ) || level.rr_timers_stopped )
	{
		return;
	}
	level.rr_timers_stopped = true;
	level.rr_hud_time setText( rr_format_time( elapsed ) );
	level.rr_hud_split setText( "" );
}

rr_destroy_after( seconds )
{
	wait seconds;
	self destroy();
}

rr_flag_is_set( name )
{
	if ( !isDefined( level.flag ) || !isDefined( level.flag[name] ) )
	{
		return false;
	}
	return level.flag[name];
}

rr_link_hud( player, y, scale )
{
	hud = NewClientHudElem( player );
	hud.foreground = true;
	hud.sort = 1;
	hud.hidewheninmenu = true;
	hud.alignX = "left";
	hud.alignY = "top";
	hud.horzAlign = "user_left";
	hud.vertAlign = "user_top";
	hud.x = 8;
	hud.y = y;
	hud.fontScale = scale;
	hud.alpha = 1;
	hud.color = ( 1, 1, 1 );
	return hud;
}

// Texte centre a l'ecran (resultat du match, compte a rebours de sortie).
rr_center_hud( player, y, scale )
{
	hud = NewClientHudElem( player );
	hud.foreground = true;
	hud.sort = 2;
	hud.hidewheninmenu = true;
	hud.alignX = "center";
	hud.alignY = "middle";
	hud.horzAlign = "center";
	hud.vertAlign = "middle";
	hud.x = 0;
	hud.y = y;
	hud.fontScale = scale;
	hud.alpha = 1;
	hud.color = ( 1, 1, 1 );
	return hud;
}

// Fin du match, pour le vainqueur comme pour le perdant : plus aucun zombie n'apparait, puis retour
// automatique au menu. Si le joueur meurt entre-temps, la fin de partie normale du jeu prend le relais.
rr_match_over( player )
{
	level endon( "end_game" );

	if ( isDefined( level.rr_match_over ) )
	{
		return;
	}
	level.rr_match_over = true;

	rr_stop_timers( getTime() - level.rr_timer_start );
	flag_clear( "spawn_zombies" );

	hud = rr_center_hud( player, -30, 1.5 );
	for ( seconds = 30; seconds > 0; seconds-- )
	{
		hud setText( "Returning to the menu in " + seconds + " s" );
		wait 1;
	}

	ExitLevel( false );
}

rr_format_time( ms )
{
	total = int( ms / 1000 );
	minutes = int( total / 60 );
	seconds = total % 60;

	if ( seconds < 10 )
	{
		return minutes + ":0" + seconds;
	}
	return minutes + ":" + seconds;
}

// Nom lisible des zones, par map. Une zone inconnue s'affiche telle quelle.
rr_zone_name( zone )
{
	if ( zone == "" )
	{
		return "";
	}
	if ( zone == "paused" )
	{
		return "GAME PAUSED";
	}

	// Les noms viennent de la map de l'adversaire, qui est la meme que la mienne.
	if ( level.script == "zombie_cod5_prototype" )
	{
		switch ( zone )
		{
			case "start_zone":		return "Starting Room";
			case "box_zone":		return "Help Room";
			case "upstairs_zone":	return "Upstairs";
		}
		return zone;
	}

	if ( level.script == "zombie_cod5_asylum" )
	{
		switch ( zone )
		{
			case "west_downstairs_zone":	return "Spawn Room";
			case "west2_downstairs_zone":	return "Spawn Room (other side)";
			case "north_downstairs_zone":	return "North Downstairs";
			case "north_upstairs_zone":		return "North Upstairs";
			case "north2_upstairs_zone":	return "North Corridor";
			case "kitchen_upstairs_zone":	return "Kitchen";
			case "power_upstairs_zone":		return "Power Room";
			case "south_upstairs_zone":		return "South Upstairs";
			case "south2_upstairs_zone":	return "South Corridor";
		}
		return zone;
	}

	switch ( zone )
	{
		case "foyer_zone":			return "Lobby";
		case "foyer2_zone":			return "Lobby (back)";
		case "vip_zone":			return "Upper Hall";
		case "dining_zone":			return "Dining Room";
		case "dressing_zone":		return "Dressing Room";
		case "stage_zone":			return "Stage";
		case "theater_zone":		return "Theater";
		case "crematorium_zone":	return "Crematorium";
		case "alleyway_zone":		return "Alley";
		case "west_balcony_zone":	return "West Balcony";
	}
	return zone;
}
